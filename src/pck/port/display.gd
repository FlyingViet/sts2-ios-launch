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

# Combat scene (background + creatures). The game stands creatures 200 units below the screen centre of a 1080-tall
# canvas and anchors the hand to the bottom; on our shorter canvas, with the hand raised, the resting hand would
# cover HP bars. The scene is lifted to keep the PC gap, shrunk around the HP-bar line if tall creatures or intents
# would then reach the top bar, and the background is enlarged if it would no longer cover the screen.
const SCENE_PIVOT_BELOW_CENTER := 240.0 # creatures stand 200 below the centre; HP bars and powers sit just below
const SCENE_MIN_SCALE := 0.7
const SCENE_TOP_MARGIN := 8.0
const SCENE_SNAP_MS := 1500 # settle the layout instantly while the room fades in, then animate
const BG_COVER_MARGIN := 30.0 # past the screen edges, so screen shake doesn't reveal them
const BG_MAX_SCALE := 1.8
const BG_FLOOR := Vector2(0, 200) # background point creatures stand on; enlarging around it keeps them on the floor

var _scene: Control = null # NCombatRoom's CombatSceneContainer
var _shake: Node = null # NScreenShake: re-applies the scene's base position every frame
var _top_row: Control = null # TopBar/LeftAlignedStuff
var _relics: Control = null
var _scene_base := Vector2.INF # the game's base position for the scene (incl. its camera offset)
var _scene_scale := 1.0 # the game's camera scaling for the encounter
var _scene_k := 1.0 # our extra scale (<= 1) so tall content stays below the top bar
var _scene_since := 0
var _scene_logged := false
var _bg_node: Node = null
var _bg_cover := Rect2() # BgContainer-local rect every full-screen background layer covers

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
	var node_scr: Script = node.get_script()
	if node_scr != null and node_scr.resource_path.get_file() == "NScreenShake.cs":
		_shake = node
	if not (node is Control):
		return
	var parent := node.get_parent()
	if String(node.name) == "CombatSceneContainer" and node.owner != null:
		var room_scr: Script = node.owner.get_script()
		if room_scr != null and room_scr.resource_path.get_file() == "NCombatRoom.cs":
			_scene = node as Control
			_scene_base = Vector2.INF
			_scene_k = 1.0
			_scene_logged = false
			return
	if String(node.name) == "LeftAlignedStuff" and parent != null and String(parent.name) == "TopBar":
		_top_row = node as Control
	elif String(node.name) == "RelicInventory":
		_relics = node as Control
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


# Lifts/shrinks the combat scene (see SCENE_* above). Positions are in the scene's parent space. The game's layout is
# "base" (its camera offset applied) at its camera scaling s0; ours moves it up by `lift`, then scales it by k around
# the HP-bar line. NScreenShake re-applies its saved base position every frame, so that is what gets changed.
func _layout_combat() -> void:
	if _scene == null:
		return
	if not is_instance_valid(_scene) or not _scene.is_inside_tree():
		_scene = null
		return
	if _scene.owner == null or not _scene.owner.is_node_ready():
		return
	if _shake == null or not is_instance_valid(_shake):
		_shake = _find_shake()
	var shaken: bool = _shake != null and _shake.get("ShakeTarget") == _scene
	if _scene_base == Vector2.INF:
		_scene_base = _shake.get("_originalTargetPosition") if shaken else _scene.position
		_scene_scale = _scene.scale.x
		_scene_since = Time.get_ticks_msec()
	var lift := maxf(0.0, (DESIGN_HEIGHT - get_viewport().get_visible_rect().size.y) * 0.5 + HAND_RAISE)
	var p0 := _scene.pivot_offset
	var s0 := _scene_scale
	var base := _scene_base - Vector2(0, lift)
	var q := Vector2(_scene.size.x * 0.5, _scene.size.y * 0.5 + SCENE_PIVOT_BELOW_CENTER)
	var k_target := _fit_scale(base, p0, s0, base + p0 + s0 * (q - p0))
	if Time.get_ticks_msec() - _scene_since < SCENE_SNAP_MS:
		_scene_k = k_target
	elif k_target < _scene_k:
		_scene_k = maxf(k_target, _scene_k - 0.01) # only ever shrinks, so intents coming and going don't pump it
	var s1 := s0 * _scene_k
	var pos := base + (1.0 - _scene_k) * s0 * (q - p0) # keeps the HP-bar line where it is when scaling
	if shaken:
		var saved: Vector2 = _shake.get("_originalTargetPosition")
		if not saved.is_equal_approx(pos):
			_shake.set("_originalTargetPosition", pos)
			_scene.position += pos - saved # keep the current shake offset; takes effect this frame
	elif not _scene.position.is_equal_approx(pos):
		_scene.position = pos
	if not is_equal_approx(_scene.scale.x, s1):
		_scene.scale = Vector2(s1, s1)
	_cover_background(pos, p0, s1)
	if not _scene_logged and Time.get_ticks_msec() - _scene_since >= SCENE_SNAP_MS:
		_scene_logged = true
		var bgc := _scene.get_node_or_null("BgContainer") as Control
		print("[PORT] combat scene: camera ", s0, " lift ", lift, " fit ", snappedf(_scene_k, 0.001), " shake ", shaken,
			" background x", snappedf(bgc.scale.x if bgc != null else 1.0, 0.001))


func _find_shake() -> Node:
	for n in get_tree().root.find_children("ScreenShake", "", true, false):
		var s: Script = n.get_script()
		if s != null and s.resource_path.get_file() == "NScreenShake.cs":
			return n
	return null


static func _global_rect(c: Control) -> Rect2:
	var xf := c.get_global_transform()
	var a := xf * Vector2.ZERO
	var b := xf * c.size
	return Rect2(a.min(b), (b - a).abs())


# Largest scale (<= 1) at which every creature's hitbox and intents stay below the top bar (and below the relic row
# where they overlap it), measured in the lifted, unscaled layout.
func _fit_scale(base: Vector2, p0: Vector2, s0: float, pivot: Vector2) -> float:
	var to_parent := (_scene.get_parent() as CanvasItem).get_global_transform().affine_inverse()
	var to_scene := _scene.get_global_transform().affine_inverse()
	var top_limit := (_global_rect(_top_row).end.y if is_instance_valid(_top_row) else 80.0) + SCENE_TOP_MARGIN
	var relic_rect := Rect2()
	if is_instance_valid(_relics) and _relics.is_visible_in_tree():
		for holder in _relics.get_children():
			if holder is Control and holder.visible:
				var r := _global_rect(holder)
				relic_rect = r if not relic_rect.has_area() else relic_rect.merge(r)
	var k := 1.0
	for container_name in ["AllyContainer", "EnemyContainer"]:
		var container := _scene.get_node_or_null(container_name)
		if container == null:
			continue
		for creature in container.get_children():
			if not (creature is Control) or not creature.is_visible_in_tree():
				continue
			var hitbox := creature.get_node_or_null("Hitbox") as Control
			if hitbox == null:
				continue
			var rect := _global_rect(hitbox)
			var intents := creature.get_node_or_null("Intents") as Control
			if intents != null:
				for icon in intents.get_children():
					if icon is Control and icon.visible:
						rect = rect.merge(_global_rect(icon))
			var a := to_scene * rect.position
			var b := to_scene * rect.end
			var top := base.y + p0.y + s0 * (minf(a.y, b.y) - p0.y)
			var left := base.x + p0.x + s0 * (minf(a.x, b.x) - p0.x)
			var right := base.x + p0.x + s0 * (maxf(a.x, b.x) - p0.x)
			var limit := top_limit
			if relic_rect.has_area() and right > relic_rect.position.x and left < relic_rect.end.x:
				limit = maxf(limit, relic_rect.end.y + SCENE_TOP_MARGIN)
			limit = (to_parent * Vector2(0, limit)).y
			if top < limit and pivot.y - top > 1.0:
				k = minf(k, (pivot.y - limit) / (pivot.y - top))
	return clampf(k, SCENE_MIN_SCALE, 1.0)


# Scales the background around the floor line just enough to keep covering the screen (plus a shake margin) after
# the scene was moved and scaled. Most encounters need no change; zoomed-out bosses can.
func _cover_background(pos: Vector2, p0: Vector2, s1: float) -> void:
	var bgc := _scene.get_node_or_null("BgContainer") as Control
	if bgc == null or bgc.get_child_count() == 0:
		return
	var bg := bgc.get_child(0)
	if bg != _bg_node:
		_bg_node = bg
		_bg_cover = _background_cover(bgc, bg)
	if not _bg_cover.has_area():
		return
	var to_parent := (_scene.get_parent() as CanvasItem).get_global_transform().affine_inverse()
	var tl := to_parent * Vector2.ZERO
	var br := to_parent * get_viewport().get_visible_rect().size
	if bgc.pivot_offset != BG_FLOOR:
		bgc.pivot_offset = BG_FLOOR
	var c := pos + p0 + s1 * (bgc.position + BG_FLOOR - p0) # the floor point in parent space
	var r := Rect2(_bg_cover.position - BG_FLOOR, _bg_cover.size) # cover rect relative to the floor point
	var m := BG_COVER_MARGIN
	var f := 1.0
	if r.position.x < 0:
		f = maxf(f, (c.x - tl.x + m) / (s1 * -r.position.x))
	if r.end.x > 0:
		f = maxf(f, (br.x + m - c.x) / (s1 * r.end.x))
	if r.position.y < 0:
		f = maxf(f, (c.y - tl.y + m) / (s1 * -r.position.y))
	if r.end.y > 0:
		f = maxf(f, (br.y + m - c.y) / (s1 * r.end.y))
	f = minf(f, BG_MAX_SCALE)
	if not is_equal_approx(bgc.scale.x, f):
		bgc.scale = Vector2(f, f)


# Intersection of the largest image of each full-screen layer (Layer_NN / Foreground), in BgContainer-local units.
static func _background_cover(bgc: Control, bg: Node) -> Rect2:
	var to_local := bgc.get_global_transform().affine_inverse()
	var cover := Rect2()
	for layer in bg.get_children():
		var layer_name := String(layer.name)
		if not (layer_name.begins_with("Layer_") or layer_name == "Foreground"):
			continue
		var best := Rect2()
		for tr in layer.find_children("*", "TextureRect", true, false):
			var xf: Transform2D = to_local * (tr as Control).get_global_transform()
			var a := xf * Vector2.ZERO
			var b := xf * (tr as Control).size
			var r := Rect2(a.min(b), (b - a).abs())
			if r.get_area() > best.get_area():
				best = r
		if best.size.x >= 2000.0 and best.size.y >= 1000.0:
			cover = best if not cover.has_area() else cover.intersection(best)
	return cover


func _before_draw() -> void:
	_layout_combat()
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
