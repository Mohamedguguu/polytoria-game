using Polytoria.Attributes;
using Polytoria.Datamodel.Data;
using Polytoria.Scripting;
using Polytoria.Shared;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Datamodel.Services;

[Static("WebSocketService"), ExplorerExclude]
[SaveIgnore]
public sealed partial class WebSocketService : Instance
{
	private const int MaxConnections   = 10;
	private const int ChunkSize        = 65536;
	private const int MaxMessageSize   = 1048576;
	private const int ConnectTimeoutMs = 10000;
	private const int SendTimeoutMs    = 5000;

	private readonly ConcurrentDictionary<string, Conn> _conns = new();

	[ScriptProperty] public PTSignal<string, string>? Disconnected    { get; private set; } = new();
	[ScriptProperty] public PTSignal<string, string>? ErrorOccurred   { get; private set; } = new();
	[ScriptProperty] public PTSignal<string, string>? MessageReceived { get; private set; } = new();

	private sealed class Conn(ClientWebSocket ws, CancellationTokenSource cts)
	{
		public readonly ClientWebSocket         WS    = ws;
		public readonly CancellationTokenSource CTS   = cts;
		public readonly ConcurrentQueue<string> Queue = new();
		public long BytesSent, BytesReceived, MsgsSent, MsgsReceived;
		public DateTime LastPong = DateTime.UtcNow;
	}

	[ScriptMethod]
	public async Task<string> ConnectAsync(string url, Dictionary<string, string>? headers = null)
	{
		ServerGuard();

		if (string.IsNullOrEmpty(url))
			throw new InvalidOperationException("need a url");

		CheckUrl(url, Root.IsLocalTest);

		if (_conns.Count >= MaxConnections)
			throw new InvalidOperationException($"already at {MaxConnections} connections");

		var ws = new ClientWebSocket();
		ws.Options.SetRequestHeader("PT-World-ID", Root.WorldID.ToString());
		ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

		if (headers != null)
			foreach (var (k, v) in headers)
				ws.Options.SetRequestHeader(k, v);

		using var connectCts = new CancellationTokenSource(ConnectTimeoutMs);
		try
		{
			await ws.ConnectAsync(new Uri(url), connectCts.Token);
		}
		catch (OperationCanceledException)
		{
			ws.Dispose();
			throw new InvalidOperationException("connection timed out after 10s");
		}

		if (!Root.IsLocalTest)
		{
			if (!ws.HttpResponseHeaders.TryGetValue("Pt-Allowed-Games", out var allowed))
			{
				await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "missing Pt-Allowed-Games", CancellationToken.None);
				ws.Dispose();
				throw new InvalidOperationException("server didn't send Pt-Allowed-Games header");
			}

			var wid   = Root.WorldID.ToString();
			var games = string.Join(",", allowed).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var ok    = false;

			foreach (var g in games)
			{
				if (g == "*" || string.Equals(g, wid, StringComparison.OrdinalIgnoreCase))
				{
					ok = true;
					break;
				}
			}

			if (!ok)
			{
				await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "game not allowed", CancellationToken.None);
				ws.Dispose();
				throw new InvalidOperationException($"game {wid} isn't whitelisted on that server");
			}
		}

		var id   = Guid.NewGuid().ToString("N")[..8];
		var conn = new Conn(ws, new CancellationTokenSource());

		_conns[id] = conn;
		_ = RecvLoop(id, conn);

		return id;
	}

	[ScriptMethod]
	public async Task SendAsync(string id, string msg)
	{
		ServerGuard();

		var conn  = Get(id);
		var bytes = Encoding.UTF8.GetBytes(msg);

		if (bytes.Length > MaxMessageSize)
			throw new InvalidOperationException($"message too large ({bytes.Length} bytes, max 1mb)");

		using var cts = new CancellationTokenSource(SendTimeoutMs);
		try
		{
			await conn.WS.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
		}
		catch (OperationCanceledException)
		{
			throw new InvalidOperationException("send timed out after 5s");
		}

		Interlocked.Add(ref conn.BytesSent, bytes.Length);
		Interlocked.Increment(ref conn.MsgsSent);
	}

	[ScriptMethod]
	public async Task SendBufferAsync(string id, byte[] data)
	{
		ServerGuard();

		var conn = Get(id);

		if (data.Length > MaxMessageSize)
			throw new InvalidOperationException($"buffer too large ({data.Length} bytes, max 1mb)");

		using var cts = new CancellationTokenSource(SendTimeoutMs);
		try
		{
			await conn.WS.SendAsync(data, WebSocketMessageType.Binary, true, cts.Token);
		}
		catch (OperationCanceledException)
		{
			throw new InvalidOperationException("send timed out after 5s");
		}

		Interlocked.Add(ref conn.BytesSent, data.Length);
		Interlocked.Increment(ref conn.MsgsSent);
	}

	[ScriptMethod]
	public List<string> DrainMessages(string id, int max = 1000)
	{
		ServerGuard();

		var conn = Get(id);
		var out_ = new List<string>(Math.Min(max, conn.Queue.Count));

		while (out_.Count < max && conn.Queue.TryDequeue(out var msg))
			out_.Add(msg);

		return out_;
	}

	[ScriptMethod]
	public int Pending(string id)
	{
		ServerGuard();
		return _conns.TryGetValue(id, out var c) ? c.Queue.Count : 0;
	}

	[ScriptMethod]
	public bool IsAlive(string id)
	{
		ServerGuard();
		if (!_conns.TryGetValue(id, out var c)) return false;
		return c.WS.State == WebSocketState.Open &&
			   (DateTime.UtcNow - c.LastPong).TotalSeconds < 60;
	}

	[ScriptMethod]
	public async Task CloseAsync(string id, string reason = "bye")
	{
		ServerGuard();

		if (!_conns.TryRemove(id, out var conn))
			return;

		if (conn.WS.State is WebSocketState.Open or WebSocketState.CloseReceived)
		{
			try { await conn.WS.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None); }
			catch { }
		}

		await conn.CTS.CancelAsync();
		conn.WS.Dispose();
		conn.CTS.Dispose();
	}

	[ScriptMethod]
	public async Task CloseAllAsync()
	{
		ServerGuard();

		var tasks = new List<Task>();
		foreach (var id in _conns.Keys)
			tasks.Add(CloseAsync(id));

		await Task.WhenAll(tasks);
	}

	[ScriptMethod]
	public string GetState(string id)
	{
		ServerGuard();
		return _conns.TryGetValue(id, out var c) ? c.WS.State.ToString() : "NotFound";
	}

	[ScriptMethod]
	public int ConnectionCount()
	{
		ServerGuard();
		return _conns.Count;
	}

	[ScriptMethod]
	public WsStats GetStats(string id)
	{
		ServerGuard();

		if (!_conns.TryGetValue(id, out var c))
			throw new InvalidOperationException($"no connection '{id}'");

		return new WsStats
		{
			BytesSent     = Interlocked.Read(ref c.BytesSent),
			BytesReceived = Interlocked.Read(ref c.BytesReceived),
			MsgsSent      = Interlocked.Read(ref c.MsgsSent),
			MsgsReceived  = Interlocked.Read(ref c.MsgsReceived),
			Backlog       = c.Queue.Count,
			State         = c.WS.State.ToString()
		};
	}

	private async Task RecvLoop(string id, Conn conn)
	{
		var buf   = ArrayPool<byte>.Shared.Rent(ChunkSize);
		var token = conn.CTS.Token;

		try
		{
			while (!token.IsCancellationRequested && conn.WS.State == WebSocketState.Open)
			{
				var result = await conn.WS.ReceiveAsync(buf, token);

				if (result.MessageType == WebSocketMessageType.Close)
				{
					_conns.TryRemove(id, out _);
					Disconnected?.Invoke(id, result.CloseStatusDescription ?? "closed");
					return;
				}

				conn.LastPong = DateTime.UtcNow;

				byte[] data;

				if (result.EndOfMessage)
				{
					Interlocked.Add(ref conn.BytesReceived, result.Count);

					if (result.Count > MaxMessageSize)
					{
						_conns.TryRemove(id, out _);
						ErrorOccurred?.Invoke(id, "message exceeded 1mb limit");
						await conn.WS.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too large", CancellationToken.None);
						return;
					}

					data = buf[..result.Count];
				}
				else
				{
					using var ms  = new System.IO.MemoryStream();
					int totalSize = result.Count;

					Interlocked.Add(ref conn.BytesReceived, result.Count);
					ms.Write(buf, 0, result.Count);

					while (!result.EndOfMessage)
					{
						result     = await conn.WS.ReceiveAsync(buf, token);
						totalSize += result.Count;

						Interlocked.Add(ref conn.BytesReceived, result.Count);

						if (totalSize > MaxMessageSize)
						{
							_conns.TryRemove(id, out _);
							ErrorOccurred?.Invoke(id, "message exceeded 1mb limit");
							await conn.WS.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too large", CancellationToken.None);
							return;
						}

						ms.Write(buf, 0, result.Count);
					}

					data = ms.ToArray();
				}

				Interlocked.Increment(ref conn.MsgsReceived);

				var msg = result.MessageType == WebSocketMessageType.Binary
					? "b:" + Convert.ToBase64String(data)
					: "t:" + Encoding.UTF8.GetString(data);

				conn.Queue.Enqueue(msg);
				MessageReceived?.Invoke(id, msg);
			}
		}
		catch (OperationCanceledException) { }
		catch (ObjectDisposedException)    { }
		catch (Exception ex) when (!token.IsCancellationRequested)
		{
			_conns.TryRemove(id, out _);
			ErrorOccurred?.Invoke(id, ex.Message);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buf);
		}
	}

	private Conn Get(string id)
	{
		if (!_conns.TryGetValue(id, out var conn))
			throw new InvalidOperationException($"no connection '{id}'");

		if (conn.WS.State != WebSocketState.Open)
			throw new InvalidOperationException("connection isn't open");

		return conn;
	}

	public static void CheckUrl(string url, bool localTest)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			throw new InvalidOperationException("invalid url");

		if (uri.Scheme != "ws" && uri.Scheme != "wss")
			throw new InvalidOperationException("url must be ws:// or wss://");

		if (localTest) return;

		if (uri.Scheme != "wss")
			throw new InvalidOperationException("only wss:// in production");

		var host = uri.Host.ToLowerInvariant();

		if (host is "localhost" or "loopback")
			throw new InvalidOperationException("no localhost in production");

		if (IPAddress.TryParse(host, out _))
			throw new InvalidOperationException("no raw IPs in production");
	}

	private void ServerGuard()
	{
		if (!Root.Network.IsServer)
			throw new InvalidOperationException("server only");
	}
}

public sealed class WsStats : IScriptObject
{
	[ScriptProperty] public long   BytesSent     { get; init; }
	[ScriptProperty] public long   BytesReceived { get; init; }
	[ScriptProperty] public long   MsgsSent      { get; init; }
	[ScriptProperty] public long   MsgsReceived  { get; init; }
	[ScriptProperty] public int    Backlog       { get; init; }
	[ScriptProperty] public string State         { get; init; } = "";
}
