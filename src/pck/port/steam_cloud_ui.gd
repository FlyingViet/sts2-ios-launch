extends CanvasLayer
## iOS port: Steam sign-in panel and status toast for Steam Cloud saves. Driven from C# (aot/Steam/PortCloud.cs).
## Built in _init so the C# side can call it before it has entered the tree.

signal login_submitted(account: String, password: String)
signal code_submitted(code: String)
signal skipped
signal cancelled

const FONT_SIZE := 30

var _root: Control
var _backdrop: ColorRect
var _panel: PanelContainer
var _title: Label
var _message: Label
var _fields: HBoxContainer
var _account: LineEdit
var _password: LineEdit
var _code: LineEdit
var _buttons: HBoxContainer
var _primary: Button
var _secondary: Button
var _toast: PanelContainer
var _toast_label: Label
var _toast_left := 0.0
var _mode := ""


func _init() -> void:
	layer = 120
	process_mode = Node.PROCESS_MODE_ALWAYS
	var theme := Theme.new()
	theme.default_font_size = FONT_SIZE
	_root = Control.new()
	_root.theme = theme
	_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_root)

	_backdrop = ColorRect.new()
	_backdrop.color = Color(0, 0, 0, 0.8)
	_backdrop.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.add_child(_backdrop)

	# Top-anchored so the landscape keyboard (bottom ~45% of the screen) never covers the fields.
	var top := MarginContainer.new()
	top.set_anchors_preset(Control.PRESET_TOP_WIDE)
	top.add_theme_constant_override("margin_top", 28)
	top.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_root.add_child(top)
	var center := CenterContainer.new()
	center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	top.add_child(center)

	_panel = PanelContainer.new()
	var box := StyleBoxFlat.new()
	box.bg_color = Color(0.08, 0.09, 0.12, 0.97)
	box.border_color = Color(0.85, 0.7, 0.35)
	box.set_border_width_all(3)
	box.set_corner_radius_all(18)
	box.set_content_margin_all(30)
	_panel.add_theme_stylebox_override("panel", box)
	_panel.custom_minimum_size = Vector2(1180, 0)
	center.add_child(_panel)

	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", 18)
	_panel.add_child(col)

	_title = Label.new()
	_title.add_theme_font_size_override("font_size", 40)
	_title.add_theme_color_override("font_color", Color(0.95, 0.82, 0.45))
	col.add_child(_title)

	_message = Label.new()
	_message.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_message.custom_minimum_size = Vector2(1100, 0)
	col.add_child(_message)

	_fields = HBoxContainer.new()
	_fields.add_theme_constant_override("separation", 18)
	col.add_child(_fields)
	_account = _line_edit("Steam account name", LineEdit.KEYBOARD_TYPE_DEFAULT)
	_password = _line_edit("Password", LineEdit.KEYBOARD_TYPE_PASSWORD)
	_password.secret = true
	_code = _line_edit("Steam Guard code", LineEdit.KEYBOARD_TYPE_DEFAULT)
	_account.text_submitted.connect(func(_t): _password.grab_focus())
	_password.text_submitted.connect(func(_t): _on_primary())
	_code.text_submitted.connect(func(_t): _on_primary())

	_buttons = HBoxContainer.new()
	_buttons.add_theme_constant_override("separation", 18)
	_buttons.alignment = BoxContainer.ALIGNMENT_END
	col.add_child(_buttons)
	_secondary = _button()
	_primary = _button()
	_secondary.pressed.connect(_on_secondary)
	_primary.pressed.connect(_on_primary)

	_toast = PanelContainer.new()
	var tbox := StyleBoxFlat.new()
	tbox.bg_color = Color(0.05, 0.06, 0.08, 0.88)
	tbox.set_corner_radius_all(14)
	tbox.content_margin_left = 26
	tbox.content_margin_right = 26
	tbox.content_margin_top = 12
	tbox.content_margin_bottom = 12
	_toast.add_theme_stylebox_override("panel", tbox)
	_toast.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_toast_label = Label.new()
	_toast_label.add_theme_font_size_override("font_size", 26)
	_toast.add_child(_toast_label)
	var toast_row := CenterContainer.new()
	toast_row.set_anchors_preset(Control.PRESET_TOP_WIDE)
	toast_row.position.y = 18
	toast_row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	toast_row.add_child(_toast)
	_root.add_child(toast_row)
	_toast.visible = false
	_set_panel_visible(false)


func _line_edit(placeholder: String, kb: int) -> LineEdit:
	var e := LineEdit.new()
	e.placeholder_text = placeholder
	e.virtual_keyboard_type = kb
	e.custom_minimum_size = Vector2(540, 76)
	e.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	e.select_all_on_focus = true
	_fields.add_child(e)
	return e


func _button() -> Button:
	var b := Button.new()
	b.custom_minimum_size = Vector2(240, 76)
	_buttons.add_child(b)
	return b


func _set_panel_visible(v: bool) -> void:
	_backdrop.visible = v
	_panel.visible = v


func _process(delta: float) -> void:
	if _toast_left > 0.0:
		_toast_left -= delta
		if _toast_left <= 0.0:
			_toast.visible = false
			if not _panel.visible:
				queue_free()


func show_login(message: String, account: String) -> void:
	_mode = "login"
	_set_panel_visible(true)
	_toast.visible = false
	_title.text = "Steam Cloud saves"
	_message.text = message
	_fields.visible = true
	_account.visible = true
	_password.visible = true
	_code.visible = false
	_buttons.visible = true
	_primary.visible = true
	_secondary.visible = true
	_primary.text = "Sign in"
	_secondary.text = "Not now"
	if account != "" and _account.text == "":
		_account.text = account
	_password.text = ""
	_primary.disabled = false


func show_code(message: String, allow_code: bool) -> void:
	_mode = "code"
	_set_panel_visible(true)
	_toast.visible = false
	_title.text = "Steam Guard"
	_message.text = message
	_fields.visible = allow_code
	_account.visible = false
	_password.visible = false
	_code.visible = allow_code
	_code.text = ""
	_buttons.visible = true
	_primary.visible = allow_code
	_primary.text = "Submit"
	_primary.disabled = false
	_secondary.visible = true
	_secondary.text = "Back"


func show_busy(message: String) -> void:
	_mode = "busy"
	_set_panel_visible(true)
	_toast.visible = false
	_title.text = "Steam Cloud saves"
	_message.text = message
	_fields.visible = false
	_buttons.visible = false
	DisplayServer.virtual_keyboard_hide()


## Hides the panel and shows a small status line; seconds <= 0 keeps it until the next call.
func show_toast(message: String, seconds: float) -> void:
	_mode = "toast"
	_set_panel_visible(false)
	DisplayServer.virtual_keyboard_hide()
	_toast_label.text = message
	_toast.visible = true
	_toast_left = seconds if seconds > 0.0 else 0.0


func close() -> void:
	DisplayServer.virtual_keyboard_hide()
	queue_free()


func _on_primary() -> void:
	match _mode:
		"login":
			var account := _account.text.strip_edges()
			if account == "" or _password.text == "":
				_message.text = "Enter your Steam account name and password."
				return
			_primary.disabled = true
			DisplayServer.virtual_keyboard_hide()
			login_submitted.emit(account, _password.text)
			_password.text = ""
		"code":
			var code := _code.text.strip_edges().to_upper()
			if code == "":
				return
			DisplayServer.virtual_keyboard_hide()
			code_submitted.emit(code)


func _on_secondary() -> void:
	DisplayServer.virtual_keyboard_hide()
	match _mode:
		"login":
			skipped.emit()
		"code":
			cancelled.emit()
