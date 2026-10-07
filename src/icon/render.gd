extends SceneTree
## Renders the app icon from the game's own Ironclad character-select animation (recovered from the player's
## pck by build.sh): a close-up of the helmet and sword, with a little extra contrast and a soft vignette.
## Needs a real renderer (run without --headless). Usage: godot --path . --script res://render.gd -- <out.png>

const SIZE := 1024
const TIME := 2.67                     # animation time of the chosen pose
const CROP := Vector3(585, 0, 480)     # x, y, size of the square crop, in pixels of a 0.25-scale render
const CONTRAST := 1.12
const SATURATION := 1.08
const VIGNETTE_DARK := 0.6             # brightness at the edges
const VIGNETTE_CENTER := Vector2(512, 563.5)
const VIGNETTE_RADIUS := 768.0
const VIGNETTE_SOFTNESS := 122.88      # Gaussian sigma of the vignette edge

func _initialize() -> void:
	var out: String = OS.get_cmdline_user_args()[0]
	var s := 0.25 * SIZE / CROP.z
	var vp := SubViewport.new()
	vp.size = Vector2i(SIZE, SIZE)
	vp.transparent_bg = false
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(vp)
	var bg := ColorRect.new()
	bg.color = Color.BLACK
	bg.size = Vector2(SIZE, SIZE)
	vp.add_child(bg)
	var sp = ClassDB.instantiate("SpineSprite")
	sp.skeleton_data_res = load("res://animations/character_select/ironclad/characterselect_ironclad_skel_data.tres")
	sp.scale = Vector2(s, s)
	vp.add_child(sp)
	await process_frame
	var st = sp.get_animation_state()
	st.set_animation("animation", true, 0)
	st.update(TIME)
	st.apply(sp.get_skeleton())
	sp.get_skeleton().update_world_transform(1)
	sp.position = Vector2(-(CROP.x / 0.25 - 1) * s, -(CROP.y / 0.25 - 13.68) * s)
	for i in 4:
		await process_frame
	await RenderingServer.frame_post_draw
	var img := vp.get_texture().get_image()
	img.convert(Image.FORMAT_RGB8)
	_enhance(img)
	var err := img.save_png(out)
	print("saved " if err == OK else "FAILED to save ", out)
	quit(0 if err == OK else 1)

static func _luma(r: int, g: int, b: int) -> float:
	return (r * 299 + g * 587 + b * 114) / 1000.0

## Normal CDF (Abramowitz-Stegun 7.1.26 erf).
static func _phi(x: float) -> float:
	var z := absf(x) / sqrt(2.0)
	var t := 1.0 / (1.0 + 0.3275911 * z)
	var erf := 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-z * z)
	return 0.5 * (1.0 + (erf if x >= 0 else -erf))

static func _enhance(img: Image) -> void:
	var w := img.get_width()
	var h := img.get_height()
	var d := img.get_data()
	var n := w * h
	var sum := 0.0
	for i in n:
		sum += _luma(d[i * 3], d[i * 3 + 1], d[i * 3 + 2])
	var mean := floorf(sum / n + 0.5)
	# Brightness factor by distance from the vignette center (blurred disk edge).
	var lut := PackedFloat32Array()
	var max_r := int(ceil(Vector2(w, h).length())) + 2
	lut.resize(max_r)
	for r in max_r:
		lut[r] = VIGNETTE_DARK + (1.0 - VIGNETTE_DARK) * _phi((VIGNETTE_RADIUS - r) / VIGNETTE_SOFTNESS)
	for y in h:
		var dy := y - VIGNETTE_CENTER.y
		for x in w:
			var i := (y * w + x) * 3
			var c := Vector3(d[i], d[i + 1], d[i + 2])
			c = (Vector3.ONE * mean).lerp(c, CONTRAST).clamp(Vector3.ZERO, Vector3.ONE * 255).round()
			var gray := roundf(_luma(int(c.x), int(c.y), int(c.z)))
			c = (Vector3.ONE * gray).lerp(c, SATURATION).clamp(Vector3.ZERO, Vector3.ONE * 255).round()
			var dx := x - VIGNETTE_CENTER.x
			var f: float = lut[int(sqrt(dx * dx + dy * dy))]
			d[i] = int(roundf(c.x * f))
			d[i + 1] = int(roundf(c.y * f))
			d[i + 2] = int(roundf(c.z * f))
	img.set_data(w, h, false, Image.FORMAT_RGB8, d)
