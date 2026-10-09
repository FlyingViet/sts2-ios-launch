extends Node
## iOS port: translates touch into mouse input.
## Tap = click, drag = press-drag, hold still >= LONG_PRESS_SEC = inspect (hover only, so tooltips
## show; slide to inspect neighbours; lifting the finger hides them without clicking).
## While a card is aiming at a target, the pointer snaps onto the nearest valid target's hitbox within
## SNAP_RADIUS (the finger hides the target and the arrow tip), except near the hand, so dragging back
## down still cancels.
## While a finger is held down, tooltips move above and to the left of it, where the thumb doesn't cover them
## (below and to the left near the top edge). Cards in the hand keep the game's placement beside the raised card.

const LONG_PRESS_SEC := 0.35
const DRAG_THRESHOLD_PX := 24.0 # raw window pixels (~8 pt on a 3x screen)
const OFFSCREEN := Vector2(-4000, -4000)
const CANCEL_CHECK_FRAMES := 3
const DEBUG := false # logs geometry + saves user://port_shots/inspect_N.png during long-press
const SNAP_RADIUS := 170.0 # canvas units from a target's hitbox
const SNAP_HAND_ZONE := 0.25 # bottom part of the screen (hand, End Turn) where the pointer never snaps
const TIP_GAP_LEFT := 40.0 # canvas units between the finger and the tooltips
const TIP_GAP_ABOVE := 56.0
const TIP_GAP_BELOW := 96.0 # the thumb's pad extends further below its contact point
const TIP_EDGE := 8.0

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
var _target_manager: Node = null # NTargetManager
var _creatures: Array[Node] = [] # NCreature
var _tip_sets: Array[Control] = [] # NHoverTipSet


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	get_window().window_input.connect(_on_window_input)
	get_tree().node_added.connect(_on_node_added)
	RenderingServer.frame_pre_draw.connect(_place_tips)
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
	pos = _snap(pos)
	var ev := InputEventMouseMotion.new()
	ev.position = pos
	ev.global_position = pos
	ev.relative = pos - _mouse
	ev.button_mask = mask
	_mouse = pos
	_emit(ev)


func _button(pos: Vector2, pressed: bool) -> void:
	pos = _snap(pos)
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
	if scr == null:
		return
	match scr.resource_path.get_file():
		"NPlayerHand.cs":
			_hand = node
		"NTargetManager.cs":
			_target_manager = node
		"NCreature.cs":
			_creatures.append(node)
		"NHoverTipSet.cs":
			_tip_sets.append(node as Control)


# Before drawing (after the game placed or re-placed its tooltips this frame): move the tooltips shown for the held
# finger, as one group, above and left of it. Tooltips that follow their owner are re-placed every frame by the game,
# so this runs every frame and only moves what isn't already in place.
func _place_tips() -> void:
	var i := _tip_sets.size() - 1
	while i >= 0:
		if not is_instance_valid(_tip_sets[i]) or not _tip_sets[i].is_inside_tree():
			_tip_sets.remove_at(i)
		i -= 1
	if _tip_sets.is_empty() or (_state != State.PENDING and _state != State.INSPECT):
		return
	var shown: Array[Control] = []
	var r := Rect2()
	var has_rect := false
	for tips in _tip_sets:
		if not tips.is_visible_in_tree():
			continue
		var owner_node = tips.get("_owner")
		if owner_node is Node and _hand != null and is_instance_valid(_hand) and _hand.is_ancestor_of(owner_node):
			continue
		for c in tips.get_children():
			var cc := c as Control
			if cc == null or not cc.visible or cc.size.x <= 0.0 or cc.size.y <= 0.0:
				continue
			r = r.merge(cc.get_global_rect()) if has_rect else cc.get_global_rect()
			has_rect = true
		shown.append(tips)
	if not has_rect:
		return
	var vp := get_viewport()
	var finger: Vector2 = vp.get_final_transform().affine_inverse() * _mouse
	var vis := vp.get_visible_rect()
	var display := get_node_or_null("/root/PortDisplay")
	var inset: float = display.get("_inset") if display != null else 0.0
	var bottom: float = display.get("_bottom_margin") if display != null else 16.0
	var lo := Vector2(vis.position.x + inset + TIP_EDGE, vis.position.y + TIP_EDGE)
	var hi := Vector2(vis.end.x - inset - TIP_EDGE, vis.end.y - bottom)
	var pos := Vector2(finger.x - TIP_GAP_LEFT - r.size.x, finger.y - TIP_GAP_ABOVE - r.size.y)
	if pos.y < lo.y:
		pos.y = finger.y + TIP_GAP_BELOW # no room above: below and to the left
	pos.x = clampf(pos.x, lo.x, maxf(lo.x, hi.x - r.size.x))
	pos.y = clampf(pos.y, lo.y, maxf(lo.y, hi.y - r.size.y))
	var d := pos - r.position
	if d.length_squared() < 0.25:
		return
	for tips in shown:
		tips.global_position += d


# Window position -> the same, or the centre of the nearest valid target's hitbox while targeting.
func _snap(pos: Vector2) -> Vector2:
	if _target_manager == null or not is_instance_valid(_target_manager) or not _target_manager.get("IsInSelection"):
		return pos
	var xf := get_viewport().get_final_transform()
	var point := xf.affine_inverse() * pos
	if point.y > get_viewport().get_visible_rect().size.y * (1.0 - SNAP_HAND_ZONE):
		return pos
	var best := Rect2()
	var best_distance := SNAP_RADIUS
	var i := _creatures.size() - 1
	while i >= 0:
		var creature := _creatures[i]
		if not is_instance_valid(creature):
			_creatures.remove_at(i)
		elif creature.is_inside_tree():
			var hitbox := creature.get_node_or_null("Hitbox") as Control
			if hitbox != null and hitbox.is_visible_in_tree() and hitbox.mouse_filter != Control.MOUSE_FILTER_IGNORE \
					and _target_manager.call("AllowedToTargetNode", creature):
				var rect := hitbox.get_global_rect()
				var distance := (point.clamp(rect.position, rect.end) - point).length()
				if distance < best_distance:
					best_distance = distance
					best = rect
		i -= 1
	if not best.has_area() or best.has_point(point):
		return pos
	return xf * best.get_center()


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
