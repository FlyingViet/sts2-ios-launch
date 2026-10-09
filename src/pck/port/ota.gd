extends Node
## iOS port: over-the-air updates for the port's scripts (res://port/*.gd).
##
## This is the first of the port's autoloads (src/pck/override.cfg), so its _init runs before the other port scripts
## are loaded: a downloaded pack mounted here replaces them for this launch. Packs come from the repository's "ota"
## GitHub release (tools/ota.sh publish) and are only used when:
##  - the manifest's RSA signature matches the public key built into the app (res://port/ota_public.pem),
##  - the pack's SHA-256 matches the signed manifest (checked again at every launch),
##  - it was made for exactly this app's compiled code (compat id, see tools/ota.sh) and is newer than this build,
##  - and it didn't stop the previous launch from reaching the main menu (then it's skipped for good).
## New packs are looked for once per launch on the main menu, downloaded, and applied at the next launch.
## Compiled code (the game patches, Steam, multiplayer) can't change this way: iOS only runs code signed at install.

const CONFIG := "res://port/ota.cfg" # written by build.sh
const PUBLIC_KEY := "res://port/ota_public.pem"
const DIR := "user://.ota" # hidden from the Files app
const MAX_PACK_BYTES := 8 * 1024 * 1024
const CHECK_DELAY_SEC := 3.0
const NOTICE_SEC := 9.0

var _enabled := false
var _url := ""
var _compat := ""
var _built_at := 0
var _key: CryptoKey
var _applied := {} # manifest of the pack mounted at startup
var _applied_is_new := false
var _menu_seen := false
var _menu: Node = null # the main menu currently shown
var _pending_notice := ""
var _notice: CanvasLayer


func _init() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	var cfg := ConfigFile.new()
	if cfg.load(CONFIG) != OK or not cfg.get_value("ota", "enabled", false):
		return
	_url = cfg.get_value("ota", "url", "")
	_compat = cfg.get_value("ota", "compat", "")
	_built_at = int(cfg.get_value("ota", "built_at", 0))
	_key = CryptoKey.new()
	if _url == "" or _compat == "" or _key.load_from_string(FileAccess.get_file_as_string(PUBLIC_KEY), true) != OK:
		print("[OTA] not configured")
		return
	_enabled = true
	_apply_installed()


func _ready() -> void:
	if _enabled:
		get_tree().node_added.connect(_on_node_added)


# ---- startup ----

func _apply_installed() -> void:
	var m := _read_verified(FileAccess.get_file_as_bytes(DIR + "/ota.json"), FileAccess.get_file_as_bytes(DIR + "/ota.json.sig"))
	if m.is_empty() or not _fits(m):
		return
	var seq := int(m["seq"])
	var path := DIR + "/" + str(m["file"])
	if FileAccess.get_sha256(path) != str(m["sha256"]):
		print("[OTA] downloaded pack ", seq, " is damaged; ignoring it")
		return
	var state := _state()
	var bad: Array = state.get_value("ota", "bad", [])
	if bad.has(seq):
		return
	if int(state.get_value("ota", "booting", 0)) == seq:
		# Set before mounting and cleared on the main menu: the last launch with this pack never got there.
		bad.append(seq)
		state.set_value("ota", "bad", bad)
		state.set_value("ota", "booting", 0)
		state.save(DIR + "/state.cfg")
		print("[OTA] pack ", seq, " didn't reach the main menu last time; using the built-in scripts")
		return
	state.set_value("ota", "booting", seq)
	state.save(DIR + "/state.cfg")
	if not ProjectSettings.load_resource_pack(ProjectSettings.globalize_path(path), true):
		print("[OTA] couldn't mount pack ", seq)
		return
	_applied = m
	_applied_is_new = int(state.get_value("ota", "applied", 0)) != seq
	if _applied_is_new:
		state.set_value("ota", "applied", seq)
		state.save(DIR + "/state.cfg")
	print("[OTA] using script update ", seq, " (", _date(seq), ")")


func _on_node_added(node: Node) -> void:
	var scr: Script = node.get_script()
	if scr != null and scr.resource_path.get_file() == "NMainMenu.cs":
		node.ready.connect(_on_main_menu.bind(node), CONNECT_ONE_SHOT)


func _on_main_menu(menu: Node) -> void:
	_menu = menu
	if _pending_notice != "":
		var text := _pending_notice
		_pending_notice = ""
		_show_notice(text)
	if _menu_seen:
		return
	_menu_seen = true
	var state := _state()
	if int(state.get_value("ota", "booting", 0)) != 0:
		state.set_value("ota", "booting", 0)
		state.save(DIR + "/state.cfg")
	if _applied_is_new:
		_show_notice("Updated to the latest port scripts (%s)." % _date(int(_applied["seq"])) + _notes(_applied))
	await get_tree().create_timer(CHECK_DELAY_SEC, true, false, true).timeout
	_check_for_update()


# ---- update check ----

func _check_for_update() -> void:
	var body := await _fetch(_url + "ota.json", 64 * 1024)
	var sig := await _fetch(_url + "ota.json.sig", 4096)
	if body.is_empty() or sig.is_empty():
		return
	var m := _read_verified(body, sig)
	if m.is_empty():
		print("[OTA] update manifest isn't signed with this app's key; ignoring it")
		return
	var seq := int(m["seq"])
	if seq <= _built_at or (not _applied.is_empty() and seq <= int(_applied["seq"])):
		print("[OTA] up to date")
		return
	var state := _state()
	if str(m.get("compat", "")) != _compat:
		if int(state.get_value("ota", "rebuild_notice", 0)) != seq:
			state.set_value("ota", "rebuild_notice", seq)
			state.save(DIR + "/state.cfg")
			_show_notice("A newer version of the iOS port is out (%s). It needs a rebuild on your Mac: git pull, then run build.sh again." % _date(seq))
		print("[OTA] update ", seq, " is for a newer build of the app")
		return
	if (state.get_value("ota", "bad", []) as Array).has(seq):
		return
	var installed := _read_verified(FileAccess.get_file_as_bytes(DIR + "/ota.json"), FileAccess.get_file_as_bytes(DIR + "/ota.json.sig"))
	if not installed.is_empty() and int(installed["seq"]) == seq \
			and FileAccess.get_sha256(DIR + "/" + str(m["file"])) == str(m["sha256"]):
		_show_notice("A script update is ready. It applies the next time you open the app.")
		return
	var size := int(m.get("size", 0))
	if size <= 0 or size > MAX_PACK_BYTES:
		return
	var pack := await _fetch(_url + str(m["file"]), MAX_PACK_BYTES)
	if pack.size() != size or _sha256(pack) != str(m["sha256"]):
		print("[OTA] download of update ", seq, " didn't match its manifest")
		return
	DirAccess.make_dir_recursive_absolute(DIR)
	# Pack first, manifest last: a manifest on disk always has its pack next to it.
	if not _write(DIR + "/" + str(m["file"]), pack) or not _write(DIR + "/ota.json.sig", sig) or not _write(DIR + "/ota.json", body):
		return
	for f in DirAccess.get_files_at(DIR):
		if f.begins_with("port-") and f != str(m["file"]) and (_applied.is_empty() or f != str(_applied["file"])):
			DirAccess.remove_absolute(DIR + "/" + f)
	print("[OTA] downloaded update ", seq)
	_show_notice("A script update was downloaded (%s). It applies the next time you open the app." % _date(seq) + _notes(m))


func _fetch(url: String, limit: int) -> PackedByteArray:
	var http := HTTPRequest.new()
	http.timeout = 30.0
	http.body_size_limit = limit
	add_child(http)
	var err := http.request(url)
	var result: Array = [HTTPRequest.RESULT_CANT_CONNECT, 0, PackedStringArray(), PackedByteArray()]
	if err == OK:
		result = await http.request_completed
	http.queue_free()
	if err != OK or result[0] != HTTPRequest.RESULT_SUCCESS or result[1] != 200:
		print("[OTA] couldn't fetch ", url.get_file(), " (", err, "/", result[0], "/", result[1], ")")
		return PackedByteArray()
	return result[3]


# ---- helpers ----

func _read_verified(body: PackedByteArray, sig: PackedByteArray) -> Dictionary:
	if body.is_empty() or sig.is_empty() or _key == null:
		return {}
	if not Crypto.new().verify(HashingContext.HASH_SHA256, _sha256_raw(body), sig, _key):
		return {}
	var m = JSON.parse_string(body.get_string_from_utf8())
	if not (m is Dictionary) or int(m.get("format", 0)) != 1 or not m.has("seq") or not m.has("file") \
			or not m.has("sha256") or not str(m["file"]).is_valid_filename():
		return {}
	return m


func _fits(m: Dictionary) -> bool:
	return str(m.get("compat", "")) == _compat and int(m["seq"]) > _built_at


func _state() -> ConfigFile:
	var state := ConfigFile.new()
	DirAccess.make_dir_recursive_absolute(DIR)
	state.load(DIR + "/state.cfg")
	return state


func _sha256_raw(data: PackedByteArray) -> PackedByteArray:
	var ctx := HashingContext.new()
	ctx.start(HashingContext.HASH_SHA256)
	ctx.update(data)
	return ctx.finish()


func _sha256(data: PackedByteArray) -> String:
	return _sha256_raw(data).hex_encode()


func _write(path: String, data: PackedByteArray) -> bool:
	var tmp := path + ".part"
	var f := FileAccess.open(tmp, FileAccess.WRITE)
	if f == null:
		return false
	f.store_buffer(data)
	f.close()
	return DirAccess.rename_absolute(tmp, path) == OK


func _date(seq: int) -> String:
	var d := Time.get_datetime_dict_from_unix_time(seq)
	return "%d-%02d-%02d" % [d["year"], d["month"], d["day"]]


func _notes(m: Dictionary) -> String:
	var n := str(m.get("notes", "")).strip_edges()
	return "" if n == "" else "\n" + n


# Shown at the top of the main menu; if a run has started meanwhile, it waits for the next main menu.
func _show_notice(text: String) -> void:
	if _menu == null or not is_instance_valid(_menu) or not _menu.is_inside_tree():
		_pending_notice = text
		return
	if _notice != null and is_instance_valid(_notice):
		_notice.queue_free()
	_notice = CanvasLayer.new()
	_notice.layer = 116
	var row := CenterContainer.new()
	row.set_anchors_preset(Control.PRESET_TOP_WIDE)
	row.offset_top = 22
	row.offset_bottom = 130
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var panel := PanelContainer.new()
	var box := StyleBoxFlat.new()
	box.bg_color = Color(0.05, 0.06, 0.08, 0.92)
	box.border_color = Color(0.95, 0.82, 0.45, 0.7)
	box.set_border_width_all(2)
	box.set_corner_radius_all(14)
	box.content_margin_left = 26
	box.content_margin_right = 26
	box.content_margin_top = 14
	box.content_margin_bottom = 14
	panel.add_theme_stylebox_override("panel", box)
	panel.gui_input.connect(func(e: InputEvent):
		if e is InputEventMouseButton and e.pressed and is_instance_valid(_notice):
			_notice.queue_free())
	var label := Label.new()
	label.text = text
	label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	label.add_theme_font_size_override("font_size", 24)
	if ResourceLoader.exists("res://themes/kreon_bold_shared.tres"):
		label.add_theme_font_override("font", load("res://themes/kreon_bold_shared.tres"))
	panel.add_child(label)
	row.add_child(panel)
	_notice.add_child(row)
	add_child(_notice)
	var notice := _notice
	get_tree().create_timer(NOTICE_SEC, true, false, true).timeout.connect(func():
		if is_instance_valid(notice):
			notice.queue_free())
