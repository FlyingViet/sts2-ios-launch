extends Node
## iOS port: translates touch into mouse input.
## Tap = click, drag = press-drag, hold still >= LONG_PRESS_SEC = inspect (hover only, so tooltips
## show; slide to inspect neighbours; lifting the finger hides them without clicking).

const LONG_PRESS_SEC := 0.35
const DRAG_THRESHOLD_PX := 24.0 # raw window pixels (~8 pt on a 3x screen)
const OFFSCREEN := Vector2(-4000, -4000)
const CANCEL_CHECK_FRAMES := 3
const DEBUG := false # logs geometry + saves user://port_shots/inspect_N.png during long-press

enum State { IDLE, PENDING, PRESSED, INSPECT }

var _state := State.IDLE
var _touch_index := -1
var _start := Vector2.ZERO
var _mouse := Vector2.ZERO
var _held := 0.0
var _play_before := false # a card was already held when this touch began
var _cancel_check := -1 # frames until we check for a card still held after release
var _cancel_after_tap := false
var _shot_pending := false
var _shot_index := 0
var _hand: Node = null # NPlayerHand (NMouseCardPlay is added as its child), tracked via node_added


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	get_window().window_input.connect(_on_window_input)
	get_tree().node_added.connect(_on_node_added)
	if DEBUG:
		var w := get_window()
		print("[PORT] window=", w.size, " content_scale=", w.content_scale_size, " visible_rect=",
			get_viewport().get_visible_rect(), " final_xform=", get_viewport().get_final_transform(),
			" safe_area=", DisplayServer.get_display_safe_area(), " screen=", DisplayServer.screen_get_size())


func _input(event: InputEvent) -> void:
	# Raw touches were already translated in _on_window_input; keep them away from the GUI.
	if event is InputEventScreenTouch or event is InputEventScreenDrag:
		get_viewport().set_input_as_handled()


func _process(delta: float) -> void:
	if _state == State.PENDING:
		_held += delta
		if _held >= LONG_PRESS_SEC:
			_state = State.INSPECT
			_shot_pending = DEBUG
	elif _state == State.INSPECT and _shot_pending:
		_held += delta
		if _held >= LONG_PRESS_SEC + 0.3:
			_shot_pending = false
			_debug_capture()
	if _cancel_check > 0:
		_cancel_check -= 1
		if _cancel_check == 0:
			_cancel_check = -1
			if _card_play_active() and (not _cancel_after_tap or _play_before):
				# Touch has no right-click: drop a card that wasn't played back into the hand.
				_right_click(_mouse)


func _on_window_input(event: InputEvent) -> void:
	if event is InputEventScreenTouch:
		var st := event as InputEventScreenTouch
		if st.pressed:
			if _touch_index != -1:
				return # single-finger only
			_touch_index = st.index
			_start = st.position
			_held = 0.0
			_state = State.PENDING
			_play_before = _card_play_active()
			_cancel_check = -1
			_motion(st.position, 0)
		elif st.index == _touch_index:
			match _state:
				State.PENDING:
					_button(st.position, true)
					_button(st.position, false)
					_cancel_after_tap = true
					_cancel_check = CANCEL_CHECK_FRAMES
				State.PRESSED:
					_motion(st.position, MOUSE_BUTTON_MASK_LEFT)
					_button(st.position, false)
					_cancel_after_tap = false
					_cancel_check = CANCEL_CHECK_FRAMES
				State.INSPECT:
					_motion(OFFSCREEN, 0)
			_state = State.IDLE
			_touch_index = -1
	elif event is InputEventScreenDrag:
		var sd := event as InputEventScreenDrag
		if sd.index != _touch_index:
			return
		match _state:
			State.PENDING:
				if sd.position.distance_to(_start) > DRAG_THRESHOLD_PX:
					_button(_start, true)
					_state = State.PRESSED
					_motion(sd.position, MOUSE_BUTTON_MASK_LEFT)
			State.PRESSED:
				_motion(sd.position, MOUSE_BUTTON_MASK_LEFT)
			State.INSPECT:
				_motion(sd.position, 0)


func _motion(pos: Vector2, mask: int) -> void:
	var ev := InputEventMouseMotion.new()
	ev.position = pos
	ev.global_position = pos
	ev.relative = pos - _mouse
	ev.button_mask = mask
	_mouse = pos
	_emit(ev)


func _button(pos: Vector2, pressed: bool) -> void:
	var ev := InputEventMouseButton.new()
	ev.position = pos
	ev.global_position = pos
	ev.button_index = MOUSE_BUTTON_LEFT
	ev.pressed = pressed
	ev.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
	_emit(ev)


func _right_click(pos: Vector2) -> void:
	for pressed in [true, false]:
		var ev := InputEventMouseButton.new()
		ev.position = pos
		ev.global_position = pos
		ev.button_index = MOUSE_BUTTON_RIGHT
		ev.pressed = pressed
		ev.button_mask = MOUSE_BUTTON_MASK_RIGHT if pressed else 0
		_emit(ev)
	if DEBUG:
		print("[PORT] cancelled held card (", "tap" if _cancel_after_tap else "drag", ")")


func _card_play_active() -> bool:
	var hand := _player_hand()
	if hand == null:
		return false
	for child in hand.get_children():
		var scr: Script = child.get_script()
		if scr != null and scr.resource_path.get_file() == "NMouseCardPlay.cs":
			return true
	return false


func _on_node_added(node: Node) -> void:
	var scr: Script = node.get_script()
	if scr != null and scr.resource_path.get_file() == "NPlayerHand.cs":
		_hand = node


func _player_hand() -> Node:
	if _hand != null and is_instance_valid(_hand) and _hand.is_inside_tree():
		return _hand
	return null


func _find_script_nodes(script_file: String) -> Array[Node]:
	var found: Array[Node] = []
	_collect(get_tree().root, script_file, found)
	return found


func _collect(node: Node, script_file: String, found: Array[Node]) -> void:
	var scr: Script = node.get_script()
	if scr != null and scr.resource_path.get_file() == script_file:
		found.append(node)
	for child in node.get_children():
		_collect(child, script_file, found)


func _debug_capture() -> void:
	var vp := get_viewport()
	var line := "[PORT] inspect touch=" + str(_mouse) + " local=" + str(vp.get_mouse_position()) + " vis=" + str(vp.get_visible_rect())
	for tip in _find_script_nodes("NHoverTipSet.cs"):
		if tip is Control:
			line += " | tipset " + str((tip as Control).get_global_rect())
			for child in tip.get_children():
				if child is Control:
					line += " " + String(child.name) + "=" + str((child as Control).get_global_rect())
	print(line)
	await RenderingServer.frame_post_draw
	var img := vp.get_texture().get_image()
	DirAccess.make_dir_recursive_absolute("user://port_shots")
	img.save_png("user://port_shots/inspect_%d.png" % _shot_index)
	_shot_index = (_shot_index + 1) % 10


func _emit(ev: InputEvent) -> void:
	# Deferred so synthesized events never re-enter the viewport while it is dispatching a touch.
	Input.call_deferred("parse_input_event", ev)
