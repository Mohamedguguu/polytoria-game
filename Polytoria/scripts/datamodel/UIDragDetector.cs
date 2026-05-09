using Godot;
using Polytoria.Attributes;
using Polytoria.Scripting;

namespace Polytoria.Datamodel;

[Instantiable]
public partial class UIDragDetector : Instance
{
	private bool _enabled = true;
	private DragAxisEnum _dragAxis = DragAxisEnum.XY;
	private Vector2 _minimumDragTranslation = new(-9999999f, -9999999f);
	private Vector2 _maximumDragTranslation = new(9999999f, 9999999f);
	private Vector2 _dragUIDelta = Vector2.Zero;

	private UIField? _parentField;
	private bool _inputConnected = false;
	private bool _dragging = false;
	private Vector2 _mouseStartGlobal = Vector2.Zero;
	private Vector2 _offsetAtDragStart = Vector2.Zero;
	private Vector2 _screenPosAtDragStart = Vector2.Zero;
	private Rect2 _viewportCache = default;

	[ScriptProperty] public PTSignal DragStart { get; private set; } = new();
	[ScriptProperty] public PTSignal DragEnd { get; private set; } = new();
	[ScriptProperty] public PTSignal DragContinue { get; private set; } = new();

	[ScriptProperty] public Vector2 DragUIDelta => _dragUIDelta;
	[ScriptProperty] public bool IsDragging => _dragging;

	[Editable, ScriptProperty, DefaultValue(true)]
	public bool Enabled
	{
		get => _enabled;
		set
		{
			if (_enabled == value) return;
			_enabled = value;
			if (!_enabled && _dragging) StopDrag();
			OnPropertyChanged();
		}
	}

	[Editable, ScriptProperty, DefaultValue(DragAxisEnum.XY)]
	public DragAxisEnum DragAxis
	{
		get => _dragAxis;
		set { _dragAxis = value; OnPropertyChanged(); }
	}

	[Editable, ScriptProperty]
	public Vector2 MinimumDragTranslation
	{
		get => _minimumDragTranslation;
		set { _minimumDragTranslation = value; OnPropertyChanged(); }
	}

	[Editable, ScriptProperty]
	public Vector2 MaximumDragTranslation
	{
		get => _maximumDragTranslation;
		set { _maximumDragTranslation = value; OnPropertyChanged(); }
	}

	public override void Init()
	{
		SetProcess(true);
		base.Init();
	}

	public override void EnterTree()
	{
		TryAttach();
		base.EnterTree();
	}

	public override void ExitTree()
	{
		Detach();
		base.ExitTree();
	}

	public override void Process(double delta)
	{
		base.Process(delta);
		if (!_dragging || _parentField == null) return;

		if (!Input.IsMouseButtonPressed(MouseButton.Left))
		{
			StopDrag();
			return;
		}

		UpdateDrag(_parentField.NodeControl.GetGlobalMousePosition());
	}

	public override void PostReparent()
	{
		base.PostReparent();
		Detach();
		TryAttach();
	}

	public override void PreDelete()
	{
		Detach();
		base.PreDelete();
	}

	private void TryAttach()
	{
		if (Parent is not UIField ui) return;
		if (_inputConnected) return;

		_parentField = ui;
		ui.NodeControl.GuiInput += OnGuiInput;
		ui.NodeControl.TreeExiting += Detach;
		ui.NodeControl.ChildEnteredTree += OnChildEnteredTree;
		ConnectChildren(ui.NodeControl);
		_inputConnected = true;
	}

	private void ConnectChildren(Control parent)
	{
		foreach (Node child in parent.GetChildren(true))
		{
			if (child is Control c)
			{
				c.GuiInput += OnGuiInput;
				ConnectChildren(c);
			}
		}
	}

	private void OnChildEnteredTree(Node child)
	{
		if (child is Control c)
		{
			c.GuiInput += OnGuiInput;
			ConnectChildren(c);
		}
	}

	private void Detach()
	{
		if (_dragging) StopDrag();

		if (_inputConnected && _parentField != null && GodotObject.IsInstanceValid(_parentField.NodeControl))
		{
			_parentField.NodeControl.GuiInput -= OnGuiInput;
			_parentField.NodeControl.TreeExiting -= Detach;
			_parentField.NodeControl.ChildEnteredTree -= OnChildEnteredTree;
			DisconnectChildren(_parentField.NodeControl);
		}

		_inputConnected = false;
		_parentField = null;
	}

	private void DisconnectChildren(Control parent)
	{
		foreach (Node child in parent.GetChildren(true))
		{
			if (child is Control c)
			{
				c.GuiInput -= OnGuiInput;
				DisconnectChildren(c);
			}
		}
	}

	private void OnGuiInput(InputEvent ev)
	{
		if (!_enabled || _parentField == null) return;

		if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed && !_dragging)
			StartDrag(mb.GlobalPosition);
	}

	private void StartDrag(Vector2 globalMousePos)
	{
		if (_parentField == null) return;
		_dragging = true;
		_mouseStartGlobal = globalMousePos;
		_offsetAtDragStart = _parentField.PositionOffset;
		_screenPosAtDragStart = _parentField.NodeControl.GlobalPosition;
		_viewportCache = _parentField.NodeControl.GetViewportRect();
		_dragUIDelta = Vector2.Zero;
		DragStart.Invoke();
	}

	private void UpdateDrag(Vector2 mouseGlobal)
	{
		if (_parentField == null) return;

		Vector2 rawDelta = mouseGlobal - _mouseStartGlobal;

		Vector2 size = _parentField.NodeControl.Size;
		Vector2 newScreenPos = _screenPosAtDragStart + rawDelta;
		newScreenPos.X = Mathf.Clamp(newScreenPos.X, 0f, _viewportCache.Size.X - size.X);
		newScreenPos.Y = Mathf.Clamp(newScreenPos.Y, 0f, _viewportCache.Size.Y - size.Y);

		Vector2 clampedDelta = newScreenPos - _screenPosAtDragStart;

		Vector2 delta = ApplyAxis(new Vector2(clampedDelta.X, -clampedDelta.Y));

		delta.X = Mathf.Clamp(delta.X, _minimumDragTranslation.X, _maximumDragTranslation.X);
		delta.Y = Mathf.Clamp(delta.Y, _minimumDragTranslation.Y, _maximumDragTranslation.Y);

		_dragUIDelta = delta;
		_parentField.PositionOffset = _offsetAtDragStart + delta;
		DragContinue.Invoke();
	}

	private void StopDrag()
	{
		_dragging = false;
		DragEnd.Invoke();
	}

	public void ResetDrag()
	{
		if (_parentField == null) return;
		_parentField.PositionOffset = _offsetAtDragStart;
		_dragUIDelta = Vector2.Zero;
	}

	private Vector2 ApplyAxis(Vector2 delta) => _dragAxis switch
	{
		DragAxisEnum.X => new Vector2(delta.X, 0f),
		DragAxisEnum.Y => new Vector2(0f, delta.Y),
		_ => delta,
	};
}
