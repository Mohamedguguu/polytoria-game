using Godot;
using Polytoria.Attributes;
using Polytoria.Shared;
using System.Collections.Generic;

namespace Polytoria.Datamodel;

[Instantiable]
public partial class Highlight : Instance
{
	private static readonly Shader _outlineShader = BuildShader();

	private Entity? _adornee;
	private Color _outlineColor = new(1, 1, 1);
	private Color _fillColor = new(1, 1, 1);
	private float _fillTransparency = 0.7f; // 0.7 feels right not too invisible
	private float _outlineTransparency = 0f;
	private float _outlineSize = 2f;
	private bool _enabled = true;
	private bool _fillVisible = true;
	private bool _outlineVisible = true;
	private bool _meshSignalConnected = false;
	private DepthModeEnum _depthMode = DepthModeEnum.AlwaysOnTop;
	private int _renderPriority = 1;

	private readonly List<(MeshInstance3D src, MeshInstance3D outline, MeshInstance3D fill)> _overlays = [];
	private ShaderMaterial? _outlineMat;
	private StandardMaterial3D? _fillMat;

	private Vector3 _lastFallbackSize = Vector3.Zero;
	private bool _usingFallback = false;

	private const string OverlayTag = "_hl_overlay";

	public enum DepthModeEnum
	{
		AlwaysOnTop,
		Occluded
	}

	[Editable, ScriptProperty]
	public Entity? Adornee
	{
		get => _adornee;
		set
		{
			if (_adornee == value) return;
			DetachMeshSignal();
			RemoveHighlight();
			_adornee = value;
			if (_enabled) TryApply();
			OnPropertyChanged();
		}
	}

	[Editable, ScriptProperty, DefaultValue(true)]
	public bool Enabled
	{
		get => _enabled;
		set
		{
			if (_enabled == value) return;
			_enabled = value;
			if (_enabled) TryApply(); else RemoveHighlight();
			OnPropertyChanged();
		}
	}

	[Editable, ScriptProperty]
	public Color OutlineColor
	{
		get => _outlineColor;
		set { _outlineColor = value; SyncMaterials(); OnPropertyChanged(); }
	}

	[Editable, ScriptProperty]
	public Color FillColor
	{
		get => _fillColor;
		set { _fillColor = value; SyncMaterials(); OnPropertyChanged(); }
	}

	[Editable, ScriptProperty, DefaultValue(0.7f)]
	public float FillTransparency
	{
		get => _fillTransparency;
		set { _fillTransparency = Mathf.Clamp(value, 0f, 1f); SyncMaterials(); OnPropertyChanged(); }
	}

	[Editable, ScriptProperty, DefaultValue(0f)]
	public float OutlineTransparency
	{
		get => _outlineTransparency;
		set { _outlineTransparency = Mathf.Clamp(value, 0f, 1f); SyncMaterials(); OnPropertyChanged(); }
	}

	[Editable, ScriptProperty, DefaultValue(2f)]
	public float OutlineSize
	{
		get => _outlineSize;
		set { _outlineSize = Mathf.Clamp(value, 0f, 20f); SyncMaterials(); OnPropertyChanged(); }
	}

	[Editable, ScriptProperty, DefaultValue(DepthModeEnum.AlwaysOnTop)]
	public DepthModeEnum DepthMode
	{
		get => _depthMode;
		set { _depthMode = value; SyncDepth(); OnPropertyChanged(); }
	}

	[Editable, ScriptProperty, DefaultValue(true)]
	public bool FillVisible
	{
		get => _fillVisible;
		set
		{
			_fillVisible = value;
			foreach (var (_, outline, fill) in _overlays)
				if (Node.IsInstanceValid(fill)) fill.Visible = value;
			OnPropertyChanged();
		}
	}

	[Editable, ScriptProperty, DefaultValue(true)]
	public bool OutlineVisible
	{
		get => _outlineVisible;
		set
		{
			_outlineVisible = value;
			foreach (var (_, outline, fill) in _overlays)
				if (Node.IsInstanceValid(outline)) outline.Visible = value;
			OnPropertyChanged();
		}
	}

	[Editable, ScriptProperty, DefaultValue(1)]
	public int RenderPriority
	{
		get => _renderPriority;
		set
		{
			_renderPriority = Mathf.Clamp(value, -128, 127);
			if (_fillMat != null) _fillMat.RenderPriority = _renderPriority;
			OnPropertyChanged();
		}
	}

	public override void Ready()
	{
		// auto assign adornee to parent if nothing set
		if (_adornee == null && Parent is Entity e)
			_adornee = e;

		if (_enabled && _adornee != null)
			TryApply();

		SetProcess(true);
		base.Ready();
	}

	public override void Process(double delta)
	{
		base.Process(delta);
		if (!_enabled || _adornee == null) return;

		// fallback path: part uses multimesh so there's no real mesh node
		// we just track the bounds size and rebuild if it changed
		if (_usingFallback)
		{
			Vector3 currentSize = _adornee.CalculateBounds().Size;
			if (!currentSize.IsEqualApprox(_lastFallbackSize))
				ApplyHighlight();
			return;
		}

		// normal path: check if any source mesh swapped its mesh resource (shape change)
		// its just a reference compare so nearly free
		foreach (var (src, outline, _) in _overlays)
		{
			if (!Node.IsInstanceValid(src)) { ApplyHighlight(); return; }
			if (src.Mesh != outline.Mesh) { ApplyHighlight(); return; }
		}
	}

	public override void PostReparent()
	{
		base.PostReparent();
		if (_adornee == null && Parent is Entity e)
			Adornee = e;
	}

	public override void PreDelete()
	{
		DetachMeshSignal();
		RemoveHighlight();
		base.PreDelete();
	}

	private void TryApply()
	{
		if (_adornee == null) return;

		// wait for mesh to finish loading before doing anything
		if (_adornee is Mesh meshEntity && meshEntity.Loading)
		{
			if (!_meshSignalConnected)
			{
				meshEntity.Loaded.Connect(OnMeshLoaded);
				_meshSignalConnected = true;
			}
			return;
		}

		ApplyHighlight();
	}

	private void OnMeshLoaded()
	{
		_meshSignalConnected = false;
		ApplyHighlight();
	}

	private void DetachMeshSignal()
	{
		if (_meshSignalConnected && _adornee is Mesh m)
		{
			m.Loaded.Disconnect(OnMeshLoaded);
			_meshSignalConnected = false;
		}
	}

	private void ApplyHighlight()
	{
		if (_adornee?.GDNode3D is not Node3D root) return;
		if (!root.IsInsideTree()) return;

		RemoveHighlight();

		_outlineMat = new ShaderMaterial { Shader = _outlineShader };
		_fillMat = new StandardMaterial3D
		{
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			CullMode = BaseMaterial3D.CullModeEnum.Back,
			RenderPriority = _renderPriority,
		};

		SyncDepth();

		List<MeshInstance3D> sources = CollectMeshes(root);

		if (sources.Count > 0)
		{
			_usingFallback = false;
			foreach (MeshInstance3D src in sources)
			{
				if (src.Mesh == null) continue;
				SpawnOverlay(src);
			}
		}
		else
		{
			_usingFallback = true;
			Aabb b = _adornee!.CalculateBounds();
			_lastFallbackSize = b.Size;
			Vector3 sz = b.Size == Vector3.Zero ? Vector3.One : b.Size;
			BoxMesh box = new() { Size = sz };
			Vector3 center = root.IsInsideTree() ? root.ToLocal(b.GetCenter()) : Vector3.Zero;
			SpawnFallbackOverlay(box, root, center);
		}

		SyncMaterials();
	}

	// overlays are children of src so they follow transforms automatically
	private void SpawnOverlay(MeshInstance3D src)
	{
		MeshInstance3D outline = new()
		{
			Mesh = src.Mesh,
			MaterialOverride = _outlineMat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = _outlineVisible,
		};
		MeshInstance3D fill = new()
		{
			Mesh = src.Mesh,
			MaterialOverride = _fillMat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = _fillVisible,
		};

		// tag them so CollectMeshes wont pick them up on next rebuild
		outline.SetMeta(OverlayTag, true);
		fill.SetMeta(OverlayTag, true);

		src.AddChild(outline);
		src.AddChild(fill);
		_overlays.Add((src, outline, fill));
	}

	private void SpawnFallbackOverlay(Godot.Mesh mesh, Node3D root, Vector3 center)
	{
		MeshInstance3D fakeSrc = new() { Mesh = mesh, Position = center };
		fakeSrc.SetMeta(OverlayTag, true);

		MeshInstance3D outline = new()
		{
			Mesh = mesh,
			MaterialOverride = _outlineMat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = _outlineVisible,
		};
		MeshInstance3D fill = new()
		{
			Mesh = mesh,
			MaterialOverride = _fillMat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = _fillVisible,
		};

		outline.SetMeta(OverlayTag, true);
		fill.SetMeta(OverlayTag, true);

		root.AddChild(fakeSrc);
		fakeSrc.AddChild(outline);
		fakeSrc.AddChild(fill);
		_overlays.Add((fakeSrc, outline, fill));
	}

	private void RemoveHighlight()
	{
		foreach (var (src, outline, fill) in _overlays)
		{
			if (Node.IsInstanceValid(outline)) outline.QueueFree();
			if (Node.IsInstanceValid(fill)) fill.QueueFree();
			// only free fakeSrc nodes we made ourselves, not real part meshes
			if (Node.IsInstanceValid(src) && src.HasMeta(OverlayTag)) src.QueueFree();
		}
		_overlays.Clear();
		_outlineMat = null;
		_fillMat = null;
	}

	private void SyncDepth()
	{
		if (_fillMat == null || _outlineMat == null) return;
		bool top = _depthMode == DepthModeEnum.AlwaysOnTop;
		_fillMat.NoDepthTest = top;
		_outlineMat.SetShaderParameter("always_on_top", top ? 1f : 0f);
	}

	private void SyncMaterials()
	{
		if (_outlineMat != null)
		{
			_outlineMat.SetShaderParameter("outline_color",
				new Color(_outlineColor.R, _outlineColor.G, _outlineColor.B, 1f - _outlineTransparency));
			_outlineMat.SetShaderParameter("outline_size", _outlineSize);
		}

		if (_fillMat != null)
			_fillMat.AlbedoColor = new Color(_fillColor.R, _fillColor.G, _fillColor.B, _fillTransparency);
	}

	private static List<MeshInstance3D> CollectMeshes(Node root)
	{
		List<MeshInstance3D> result = [];
		Stack<Node> stack = new();
		stack.Push(root);

		while (stack.Count > 0)
		{
			Node node = stack.Pop();
			if (node.HasMeta(OverlayTag)) continue; // skip overlay nodes
			if (node is MeshInstance3D m && m.Mesh != null)
				result.Add(m);
			foreach (Node child in node.GetChildren(true))
				stack.Push(child);
		}

		return result;
	}

	private static Shader BuildShader()
	{
		Shader s = new();
		s.Code =
			"shader_type spatial;\n" +
			"render_mode cull_front, unshaded, depth_draw_never;\n" +
			"uniform vec4 outline_color : source_color = vec4(1.0);\n" +
			"uniform float outline_size : hint_range(0.0, 20.0, 0.1) = 2.0;\n" +
			"uniform float always_on_top : hint_range(0.0, 1.0, 1.0) = 1.0;\n" +
			"\n" +
			"void vertex() {\n" +
			"    vec4 clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));\n" +
			"    vec4 clip_npos = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX + NORMAL * 0.01, 1.0));\n" +
			"    vec2 offset = normalize(clip_npos.xy / clip_npos.w - clip.xy / clip.w);\n" +
			"    clip.xy += offset / VIEWPORT_SIZE * outline_size * clip.w * 2.0;\n" +
			"    if (always_on_top > 0.5) clip.z = clip.w * 0.0001;\n" +
			"    POSITION = clip;\n" +
			"}\n" +
			"\n" +
			"void fragment() {\n" +
			"    ALBEDO = outline_color.rgb;\n" +
			"    ALPHA = outline_color.a;\n" +
			"}\n";
		return s;
	}
}
