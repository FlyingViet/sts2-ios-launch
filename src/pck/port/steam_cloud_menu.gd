extends Node
## iOS port: Steam Cloud status pill on the main menu (under the profile button) and a panel to check sync
## status and manually pull or push saves. Status and actions come from C# (aot/Steam/PortCloud.cs) through
## callables stored in Engine metadata: "port_cloud_status" -> Dictionary, "port_cloud_action"(String) -> bool.

const POLL_SECONDS := 0.3
const MONTHS := ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
const GOLD := Color(0.95, 0.82, 0.45)

var _font: Font
var _font_bold: Font
var _menu: Control            # current NMainMenu
var _profile_button: Control  # its ChangeProfileButton; the pill mirrors its visibility
var _pill: Button
var _layer: CanvasLayer       # open panel
var _status_label: RichTextLabel
var _busy_label: Label
var _result_label: Label
var _confirm_label: Label
var _buttons: HBoxContainer
var _pull_button: Button
var _push_button: Button
var _close_button: Button
var _confirm_buttons: HBoxContainer
var _confirm_ok: Button
var _confirm_action := ""
var _status := {}
var _since_poll := 0.0


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	if ResourceLoader.exists("res://themes/kreon_regular_shared.tres"):
		_font = load("res://themes/kreon_regular_shared.tres")
	if ResourceLoader.exists("res://themes/kreon_bold_shared.tres"):
		_font_bold = load("res://themes/kreon_bold_shared.tres")
	get_tree().node_added.connect(_on_node_added)


func _on_node_added(node: Node) -> void:
	var scr: Script = node.get_script()
	if scr != null and scr.resource_path.get_file() == "NMainMenu.cs":
		node.ready.connect(_attach_pill.bind(node), CONNECT_ONE_SHOT)


func _attach_pill(menu: Control) -> void:
	_profile_button = menu.get_node_or_null("ChangeProfileButton")
	if _profile_button == null:
		return
	_menu = menu
	_pill = Button.new()
	_pill.name = "PortSteamCloudPill"
	_pill.focus_mode = Control.FOCUS_NONE
	_pill.add_theme_font_size_override("font_size", 22)
	if _font_bold != null:
		_pill.add_theme_font_override("font", _font_bold)
	for state in ["normal", "hover", "pressed", "focus"]:
		var box := StyleBoxFlat.new()
		box.bg_color = Color(0.06, 0.07, 0.1, 0.72 if state != "pressed" else 0.9)
		box.border_color = Color(GOLD, 0.55)
		box.set_border_width_all(2)
		box.set_corner_radius_all(16)
		box.content_margin_left = 16
		box.content_margin_right = 16
		box.content_margin_top = 6
		box.content_margin_bottom = 6
		_pill.add_theme_stylebox_override(state, box)
	_pill.add_theme_color_override("font_color", Color(0.92, 0.92, 0.95))
	_pill.add_theme_color_override("font_hover_color", Color.WHITE)
	_pill.add_theme_color_override("font_pressed_color", GOLD)
	_pill.pressed.connect(_open_panel)
	menu.add_child(_pill)
	menu.move_child(_pill, _profile_button.get_index() + 1)
	_poll()
	_update_pill()


func _process(delta: float) -> void:
	var pill_alive := _pill != null and is_instance_valid(_pill) and _pill.is_inside_tree()
	if pill_alive and is_instance_valid(_profile_button):
		# Follow the profile button: its x includes the safe-area inset and it hides while submenus are open.
		_pill.position = Vector2(_profile_button.position.x + 8.0, _profile_button.position.y + _profile_button.size.y + 6.0)
		_pill.visible = _profile_button.visible
		_pill.modulate = _profile_button.modulate
	if not pill_alive and _layer == null:
		return
	_since_poll += delta
	if _since_poll >= POLL_SECONDS:
		_since_poll = 0.0
		_poll()
		if pill_alive:
			_update_pill()
		if _layer != null:
			_update_panel()


func _poll() -> void:
	if Engine.has_meta("port_cloud_status"):
		_status = Engine.get_meta("port_cloud_status").call()


func _action(name: String) -> void:
	if Engine.has_meta("port_cloud_action"):
		Engine.get_meta("port_cloud_action").call(name)
	_poll()
	_update_panel()


# ---- pill ----

func _state_text() -> String:
	if _status.is_empty():
		return "Unavailable"
	if _status.syncing:
		return "Syncing…"
	if _status.busy != "":
		return "Working…"
	if not _status.signed_in:
		return "Not signed in"
	if not _status.online:
		return "Offline · reconnecting" if _status.get("reconnecting", false) else "Offline"
	if _status.get("held", 0) > 0:
		return "Newer saves on Steam"
	if _status.pending > 0:
		return "Uploading %d…" % _status.pending
	var c: Dictionary = _status.get("compare", {})
	if not c.is_empty() and (c.newer_on_steam + c.only_on_steam + c.both_changed) > 0:
		return "Changes on Steam"
	if not c.is_empty() and (c.not_uploaded + c.only_here) > 0:
		return "Not uploaded"
	return "Synced"


func _update_pill() -> void:
	_pill.text = "Steam Cloud · " + _state_text()


# ---- panel ----

func _open_panel() -> void:
	if _layer != null:
		return
	_layer = CanvasLayer.new()
	_layer.layer = 110
	var root := Control.new()
	root.set_anchors_preset(Control.PRESET_FULL_RECT)
	var theme := Theme.new()
	theme.default_font_size = 28
	if _font != null:
		theme.default_font = _font
	root.theme = theme
	_layer.add_child(root)

	var dim := ColorRect.new()
	dim.color = Color(0, 0, 0, 0.75)
	dim.set_anchors_preset(Control.PRESET_FULL_RECT)
	dim.gui_input.connect(func(e: InputEvent):
		if e is InputEventMouseButton and e.pressed and _status.get("busy", "") == "":
			_close_panel())
	root.add_child(dim)

	var center := CenterContainer.new()
	center.set_anchors_preset(Control.PRESET_FULL_RECT)
	center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	root.add_child(center)
	var panel := PanelContainer.new()
	var box := StyleBoxFlat.new()
	box.bg_color = Color(0.08, 0.09, 0.12, 0.97)
	box.border_color = GOLD
	box.set_border_width_all(3)
	box.set_corner_radius_all(18)
	box.set_content_margin_all(32)
	panel.add_theme_stylebox_override("panel", box)
	panel.custom_minimum_size = Vector2(1100, 0)
	center.add_child(panel)
	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", 16)
	panel.add_child(col)

	var title := Label.new()
	title.text = "Steam Cloud saves"
	title.add_theme_font_size_override("font_size", 40)
	title.add_theme_color_override("font_color", GOLD)
	if _font_bold != null:
		title.add_theme_font_override("font", _font_bold)
	col.add_child(title)

	_status_label = RichTextLabel.new()
	_status_label.bbcode_enabled = true
	_status_label.fit_content = true
	_status_label.scroll_active = false
	_status_label.custom_minimum_size = Vector2(1036, 0)
	_status_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	if _font != null:
		_status_label.add_theme_font_override("normal_font", _font)
	if _font_bold != null:
		_status_label.add_theme_font_override("bold_font", _font_bold)
	_status_label.add_theme_font_size_override("normal_font_size", 28)
	_status_label.add_theme_font_size_override("bold_font_size", 28)
	col.add_child(_status_label)

	_busy_label = Label.new()
	_busy_label.add_theme_color_override("font_color", GOLD)
	col.add_child(_busy_label)
	_result_label = Label.new()
	_result_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_result_label.custom_minimum_size = Vector2(1036, 0)
	col.add_child(_result_label)
	_confirm_label = Label.new()
	_confirm_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_confirm_label.custom_minimum_size = Vector2(1036, 0)
	_confirm_label.add_theme_color_override("font_color", Color(1.0, 0.78, 0.6))
	col.add_child(_confirm_label)

	_buttons = HBoxContainer.new()
	_buttons.add_theme_constant_override("separation", 18)
	_buttons.alignment = BoxContainer.ALIGNMENT_END
	col.add_child(_buttons)
	_pull_button = _make_button(_buttons, "Pull from Steam", func(): _ask_confirm("pull"))
	_push_button = _make_button(_buttons, "Push to Steam", func(): _ask_confirm("push"))
	_close_button = _make_button(_buttons, "Close", _close_panel)

	_confirm_buttons = HBoxContainer.new()
	_confirm_buttons.add_theme_constant_override("separation", 18)
	_confirm_buttons.alignment = BoxContainer.ALIGNMENT_END
	col.add_child(_confirm_buttons)
	_make_button(_confirm_buttons, "Cancel", func(): _ask_confirm(""))
	_confirm_ok = _make_button(_confirm_buttons, "", _run_confirmed)

	get_tree().root.add_child(_layer)
	_ask_confirm("")
	_action("check")


func _make_button(parent: Container, text: String, on_press: Callable) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	for state in ["normal", "hover", "pressed", "disabled"]:
		var box := StyleBoxFlat.new()
		box.bg_color = Color(0.16, 0.17, 0.22) if state != "pressed" else Color(0.3, 0.26, 0.14)
		box.border_color = Color(GOLD, 0.25 if state == "disabled" else 0.8)
		box.set_border_width_all(2)
		box.set_corner_radius_all(12)
		b.add_theme_stylebox_override(state, box)
	b.add_theme_color_override("font_disabled_color", Color(1, 1, 1, 0.3))
	b.custom_minimum_size = Vector2(250, 76)
	b.pressed.connect(on_press)
	parent.add_child(b)
	return b


func _close_panel() -> void:
	if _layer != null:
		_layer.queue_free()
	_layer = null


func _ask_confirm(action: String) -> void:
	_confirm_action = action
	match action:
		"pull":
			_confirm_label.text = "Replace this iPhone's saves with the ones in Steam Cloud? Progress on this iPhone " \
				+ "that isn't in Steam Cloud will be replaced. A backup of the current iPhone saves is kept on this iPhone."
			_confirm_ok.text = "Pull"
		"push":
			_confirm_label.text = "Replace the saves in Steam Cloud with this iPhone's? Progress from other devices " \
				+ "that isn't on this iPhone will be replaced. A backup of the replaced Steam Cloud saves is kept on this iPhone."
			_confirm_ok.text = "Push"
	_update_panel()


func _run_confirmed() -> void:
	var action := _confirm_action
	_ask_confirm("")
	if action != "":
		_action(action)


func _update_panel() -> void:
	if _layer == null or _status_label == null:
		return
	var s := _status
	var lines := PackedStringArray()
	if s.is_empty():
		lines.append("Steam Cloud isn't available in this build.")
	else:
		lines.append("[b]Account:[/b] %s" % (s.account if s.signed_in else "not signed in"))
		lines.append("[b]Status:[/b] %s" % _state_text())
		lines.append("[b]Last synced:[/b] %s" % _format_time(int(s.last_sync)))
		lines.append("[b]Steam Cloud:[/b] %d save files    [b]Waiting to upload:[/b] %d" % [s.cloud_files, s.pending])
		var c: Dictionary = s.get("compare", {})
		if not c.is_empty():
			var when := _format_time(int(c.checked))
			lines.append("[b]Compared %s:[/b] %s" % [when[0].to_lower() + when.substr(1), _compare_text(c)])
		if s.signed_in and not s.online:
			lines.append("Playing offline. Your progress is saved on this iPhone and syncs automatically when Steam is reachable again.")
		if s.get("held", 0) > 0:
			lines.append("%d save file(s) changed on another device while you were offline. They load automatically on the main menu." % s.held)
		if s.last_error != "":
			lines.append("[color=#ff9a8a][b]Last error:[/b] %s[/color]" % s.last_error)
		if not s.signed_in:
			lines.append("Close and reopen the game to sign in to Steam.")
	_status_label.text = "\n".join(lines)
	var busy: String = s.get("busy", "")
	if s.get("syncing", false):
		busy = "Syncing with Steam Cloud…"
	_busy_label.text = busy
	_busy_label.visible = busy != ""
	_result_label.text = s.get("result", "")
	_result_label.visible = _result_label.text != "" and busy == ""
	var confirming := _confirm_action != ""
	_confirm_label.visible = confirming
	_confirm_buttons.visible = confirming
	_buttons.visible = not confirming
	var can := bool(s.get("can_transfer", false))
	_pull_button.disabled = not can
	_push_button.disabled = not can
	_close_button.disabled = busy != ""
	_confirm_ok.disabled = not can


func _compare_text(c: Dictionary) -> String:
	if c.up_to_date:
		return "identical to Steam Cloud (%d files)" % c.same
	var parts := PackedStringArray()
	if c.newer_on_steam > 0:
		parts.append("%d newer on Steam" % c.newer_on_steam)
	if c.only_on_steam > 0:
		parts.append("%d only on Steam" % c.only_on_steam)
	if c.not_uploaded > 0:
		parts.append("%d newer on this iPhone" % c.not_uploaded)
	if c.only_here > 0:
		parts.append("%d only on this iPhone" % c.only_here)
	if c.both_changed > 0:
		parts.append("%d changed on both" % c.both_changed)
	return ", ".join(parts)


func _format_time(unix: int) -> String:
	if unix <= 0:
		return "Never"
	var now := int(Time.get_unix_time_from_system())
	if now - unix < 60:
		return "Just now"
	var bias := int(Time.get_time_zone_from_system().bias) * 60
	var d := Time.get_datetime_dict_from_unix_time(unix + bias)
	var today := Time.get_datetime_dict_from_unix_time(now + bias)
	var hour: int = d.hour % 12
	if hour == 0:
		hour = 12
	var clock := "%d:%02d %s" % [hour, d.minute, "AM" if d.hour < 12 else "PM"]
	if d.year == today.year and d.month == today.month and d.day == today.day:
		return "Today " + clock
	return "%s %d, %s" % [MONTHS[d.month - 1], d.day, clock]
