extends Node
## iOS port: full-bleed widescreen (no pillarbox) and a larger UI.
## The game re-applies its own aspect-ratio setting (Keep 16:9 by default), so this is enforced every frame.

const UI_HEIGHT := 960 # logical canvas height; game default 1080 -> lower = bigger UI (960 = +12.5%)
const MIN_WIDTH := 1680 # same minimum width the game uses for its own "Auto" (Expand) mode
const DESIGN_HEIGHT := 1080.0 # canvas height the game's hard-coded positions assume
const HAND_RAISE := 64.0 # canvas units the hand is lifted so card drags start above the iOS home-swipe zone
const STAR_SHIFT := Vector2(-34, -4) # Regent star counter moved off the energy orb (the PC layout overlaps them)
const DEBUG_SHOTS := false # saves user://port_shots/auto_NN.jpg every SHOT_INTERVAL seconds
# Runtime switch: if user://port_debug_shots exists, take as many shots as the number it contains
# (default 5), then delete it. Push it with `devicectl device copy to` -- no rebuild needed.
const SHOT_FLAG := "user://port_debug_shots"
const SHOT_INTERVAL := 4.0

# UI containers pulled inside the iOS safe area (rounded corners / Dynamic Island) while backgrounds stay
# full-bleed. Key = node name, value = required parent name ("" = any).
const INSET_TARGETS := {
	"LeftAlignedStuff": "TopBar",
	"RightAlignedStuff": "TopBar",
	"RelicInventory": "GlobalUi",
	"MultiplayerPlayerContainer": "GlobalUi",
	"DebugInfo": "GlobalUi",
	"EnergyCounterContainer": "CombatUi",
	"StarCounter": "CombatUi",
	"CombatPileContainer": "CombatUi",
	"ChangeProfileButton": "MainMenu",
	"PatchNotesButton": "MainMenu",
	"ReleaseInfo": "MainMenu",
	"ModdedWarning": "MainMenu",
	"ModWarningContainer": "MainMenu",
}
# NEndTurnButton places itself at a fixed fraction of the viewport width (1604/1920), so it is
# pulled in by the inset right before drawing (after tweens have run).
const END_TURN_X_RATIO := 1604.0 / 1920.0
# Nodes the game tweens to absolute y positions designed for a 1080-tall canvas. Key = node name,
# value = script file of the scene root that owns it. Clamped so their bottom stays on screen.
const BOTTOM_CLAMP := {
	"AcknowledgeButton": ["NTimelineTutorial.cs"],
	"Description": ["NRestSiteRoom.cs", "NBestiary.cs"],
}

var _inset := 0.0 # logical canvas units
var _targets: Array[Control] = []
var _back_buttons: Array[Control] = [] # NBackButton: slides between _showPos/_hidePos (set in its _Ready)
var _end_turn_buttons: Array[Control] = []
var _bottom_clamped: Array[Control] = []
var _bottom_margin := 0.0 # logical units kept clear above the home indicator
var _shots_left := -1 # -1 = unlimited (DEBUG_SHOTS), 0 = off
var _since_shot := 0.0
var _shot_index := 0
var _last_rect := Rect2()


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	_shots_left = -1 if DEBUG_SHOTS else 0
	if FileAccess.file_exists(SHOT_FLAG):
		var n := FileAccess.get_file_as_string(SHOT_FLAG).strip_edges().to_int()
		_shots_left = n if n > 0 else 5
		DirAccess.remove_absolute(SHOT_FLAG)
	_enforce() # computes _inset before any game UI is added
	get_tree().node_added.connect(_on_node_added)
	RenderingServer.frame_pre_draw.connect(_before_draw)


func _on_node_added(node: Node) -> void:
	if not (node is Control):
		return
	var parent := node.get_parent()
	if parent is Control and String(parent.name) == "EnergyCounterContainer":
		# NCombatUi.Activate adds the energy counter right after (maybe) moving this container and right before
		# reparenting the star counter onto it, so fix the container now.
		_restore_bottom_anchor(parent as Control)
	if String(node.name) == "StarCounter" and parent != null and parent.get_parent() != null \
			and String(parent.get_parent().name) == "EnergyCounterContainer":
		# Reparented onto the energy counter; Reparent restores its global position after this signal.
		_shift_star.call_deferred(node)
	if String(node.name) == "CardHolderContainer" and node.owner != null:
		var hand_scr: Script = node.owner.get_script()
		if hand_scr != null and hand_scr.resource_path.get_file() == "NPlayerHand.cs":
			var holder := node as Control
			if not holder.has_meta("port_orig_y"):
				holder.set_meta("port_orig_y", Vector2(holder.offset_top, holder.offset_bottom))
			var orig_y: Vector2 = holder.get_meta("port_orig_y")
			holder.offset_top = orig_y.x - HAND_RAISE
			holder.offset_bottom = orig_y.y - HAND_RAISE
			return
	var scr: Script = node.get_script()
	if scr != null:
		match scr.resource_path.get_file():
			"NBackButton.cs":
				_back_buttons.append(node as Control)
				return
			"NEndTurnButton.cs":
				_end_turn_buttons.append(node as Control)
				return
	if BOTTOM_CLAMP.has(String(node.name)) and node.owner != null:
		var owner_scr: Script = node.owner.get_script()
		if owner_scr != null and owner_scr.resource_path.get_file() in BOTTOM_CLAMP[String(node.name)]:
			_bottom_clamped.append(node as Control)
		return
	if not INSET_TARGETS.has(String(node.name)):
		return
	var parent_name: String = INSET_TARGETS[String(node.name)]
	if parent_name != "" and (node.get_parent() == null or String(node.get_parent().name) != parent_name):
		return
	var c := node as Control
	if not c.has_meta("port_orig"):
		c.set_meta("port_orig", Vector2(c.offset_left, c.offset_right))
	_targets.append(c)
	# Apply now, before children run _Ready (some cache their positions there, e.g. the pile buttons).
	_inset_control(c)


# NCombatUi.Activate (Regent only) calls EnergyCounterContainer.SetPosition((100, 806), keepOffsets) -- a point on a
# 1080-tall canvas, applied by moving the anchors. Turn it back into a bottom-left anchored point at the same
# distance from the bottom, keeping the safe-area inset.
func _restore_bottom_anchor(c: Control) -> void:
	if is_equal_approx(c.anchor_left, 0.0) and is_equal_approx(c.anchor_top, 1.0) \
			and is_equal_approx(c.anchor_right, 0.0) and is_equal_approx(c.anchor_bottom, 1.0):
		return
	var p := c.position
	var sz := c.size
	c.anchor_left = 0.0
	c.anchor_right = 0.0
	c.anchor_top = 1.0
	c.anchor_bottom = 1.0
	c.set_meta("port_orig", Vector2(p.x, p.x + sz.x))
	c.offset_top = p.y - DESIGN_HEIGHT
	c.offset_bottom = c.offset_top + sz.y
	_inset_control(c)


func _shift_star(star: Control) -> void:
	if is_instance_valid(star) and not star.has_meta("port_star_shifted"):
		star.set_meta("port_star_shifted", true)
		star.position += STAR_SHIFT


func _edge_delta(anchor: float) -> float:
	# Left-anchored edges move right, right-anchored edges move left, centred edges stay.
	if anchor < 0.4:
		return _inset
	if anchor > 0.6:
		return -_inset
	return 0.0


func _inset_control(c: Control) -> void:
	var orig: Vector2 = c.get_meta("port_orig")
	var left := orig.x + _edge_delta(c.anchor_left)
	var right := orig.y + _edge_delta(c.anchor_right)
	if not is_equal_approx(c.offset_left, left) or not is_equal_approx(c.offset_right, right):
		c.offset_left = left
		c.offset_right = right


func _before_draw() -> void:
	var vis := get_viewport().get_visible_rect().size
	var vw := vis.x
	var j := _bottom_clamped.size() - 1
	while j >= 0:
		var c := _bottom_clamped[j]
		if not is_instance_valid(c) or not c.is_inside_tree():
			_bottom_clamped.remove_at(j)
		elif c.is_visible_in_tree():
			var h := c.size.y * c.get_global_transform().get_scale().y
			var max_y := vis.y - _bottom_margin - h
			if c.global_position.y > max_y:
				c.global_position.y = max_y
		j -= 1
	var i := _end_turn_buttons.size() - 1
	while i >= 0:
		var b := _end_turn_buttons[i]
		if not is_instance_valid(b) or not b.is_inside_tree():
			_end_turn_buttons.remove_at(i)
		else:
			var x := END_TURN_X_RATIO * vw - _inset
			if not is_equal_approx(b.position.x, x):
				b.position.x = x
		i -= 1


func _apply_insets() -> void:
	var i := _targets.size() - 1
	while i >= 0:
		var c := _targets[i]
		if not is_instance_valid(c) or not c.is_inside_tree():
			_targets.remove_at(i)
		elif INSET_TARGETS[String(c.name)] != "" and String(c.get_parent().name) != INSET_TARGETS[String(c.name)]:
			# Reparented (the star counter moves onto the energy counter): it now follows its new, inset parent.
			_targets.remove_at(i)
		else:
			_inset_control(c)
		i -= 1
	i = _back_buttons.size() - 1
	while i >= 0:
		var b := _back_buttons[i]
		if not is_instance_valid(b) or not b.is_inside_tree():
			_back_buttons.remove_at(i)
		elif b.is_node_ready():
			_shift_back_button(b)
		i -= 1


func _shift_back_button(b: Control) -> void:
	var show_pos = b.get("_showPos")
	var hide_pos = b.get("_hidePos")
	if not (show_pos is Vector2) or not (hide_pos is Vector2):
		return
	var applied: float = b.get_meta("port_shift", 0.0)
	var base_x: float = b.get_meta("port_show_x", INF)
	if applied != 0.0 and not is_equal_approx(show_pos.x, base_x + applied):
		applied = 0.0 # the game recomputed its positions (e.g. on resize)
	if applied == 0.0:
		base_x = show_pos.x
	if is_equal_approx(applied, _inset):
		return
	var d := _inset - applied
	b.set("_showPos", show_pos + Vector2(d, 0))
	b.set("_hidePos", hide_pos + Vector2(d, 0))
	b.position.x += d
	b.set_meta("port_show_x", base_x)
	b.set_meta("port_shift", _inset)


func _process(delta: float) -> void:
	_enforce()
	_apply_insets()
	if _shots_left != 0:
		_since_shot += delta
		if _since_shot >= SHOT_INTERVAL:
			_since_shot = 0.0
			if _shots_left > 0:
				_shots_left -= 1
			_capture()


func _enforce() -> void:
	var w := get_window()
	var size := Vector2i(MIN_WIDTH, UI_HEIGHT)
	if w.content_scale_aspect != Window.CONTENT_SCALE_ASPECT_EXPAND or w.content_scale_size != size:
		w.content_scale_aspect = Window.CONTENT_SCALE_ASPECT_EXPAND
		w.content_scale_size = size
	var rect := get_viewport().get_visible_rect()
	if rect != _last_rect:
		_last_rect = rect
		var safe := DisplayServer.get_display_safe_area()
		var raw_inset := maxf(safe.position.x, w.size.x - safe.end.x)
		_inset = raw_inset * rect.size.y / w.size.y # raw px -> logical canvas units
		_bottom_margin = maxf(16.0, (w.size.y - safe.end.y) * rect.size.y / w.size.y)
		print("[PORT] canvas ", rect.size, " (window ", w.size, ") safe inset ", raw_inset, "px = ", _inset)


func _capture() -> void:
	await RenderingServer.frame_post_draw
	var img := get_viewport().get_texture().get_image()
	DirAccess.make_dir_recursive_absolute("user://port_shots")
	img.save_jpg("user://port_shots/auto_%02d.jpg" % _shot_index, 0.7)
	_shot_index = (_shot_index + 1) % 20
