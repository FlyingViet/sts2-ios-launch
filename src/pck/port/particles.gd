extends Node
## iOS port: shortens particle "pre-process" (fast-forward) work that froze the game for up to ~1.5 s.
##
## Many effects pre-process on their first frame so they appear already running: the menu clouds 400 s, boss fogs
## 100-600 s. Godot simulates that in 1/30 s steps (1/fixed_fps if set) inside a single frame: 12,000+ steps per
## effect, ~40 us each on the iPhone's GPU (each step is a buffer upload, a compute dispatch and barriers) and
## similar on the CPU for CPUParticles2D. Godot ignores speed_scale while pre-processing, so instead:
##  1. Continuous emitters (not one-shot) never pre-process longer than about one lifetime: by then every particle
##     has been re-emitted, so the result looks the same.
##  2. If that is still more than MAX_STEPS steps, step coarser (at least MIN_STEPS_PER_LIFETIME steps per lifetime):
##     CPUParticles2D get a lower fixed_fps only until their first update (it's a plain setter there); GPUParticles2D
##     keep it (changing it later restarts them), so only slow, long-lived ones are changed, with interpolation on.
##  3. Short-lived GPU effects still over MAX_STEPS (e.g. an event scene with 75 spark emitters) start without
##     pre-process and are fast-forwarded with request_particles_process() over the next frames, at most
##     GPU_STEPS_PER_FRAME steps per frame in total: the same simulation, spread out instead of one long freeze.
## Applied when a node enters the tree, before its first update.

const MAX_STEPS := 240
const MIN_STEPS_PER_LIFETIME := 10.0
const LIFETIME_MARGIN := 1.05
const GPU_MIN_LIFETIME := 10.0
const GPU_STEPS_PER_FRAME := 360

var _pending: Array = [] # [CPUParticles2D, original fixed_fps, frames seen processing]
var _warming: Array = [] # [GPUParticles2D, seconds left to fast-forward, original preprocess, frames seen processing]
var _tuned := 0
var _steps_before := 0
var _steps_after := 0


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	get_tree().node_added.connect(_on_node_added)
	RenderingServer.frame_post_draw.connect(_after_draw)


func _on_node_added(node: Node) -> void:
	if node is CPUParticles2D:
		_tune(node, true)
	elif node is GPUParticles2D:
		_tune(node, false)


func _tune(node: Node2D, cpu: bool) -> void:
	var pre: float = node.preprocess
	if pre <= 0.0:
		return
	var life: float = maxf(node.lifetime, 0.001)
	var fps: int = node.fixed_fps if node.fixed_fps > 0 else 30
	var before := ceili(pre * fps)
	if before <= MAX_STEPS:
		return
	if not node.one_shot and pre > life * LIFETIME_MARGIN + 0.1:
		pre = life * LIFETIME_MARGIN + 0.1
		node.preprocess = pre
	var after := ceili(pre * fps)
	if after > MAX_STEPS:
		var f := maxi(1, maxi(floori(MAX_STEPS / pre), ceili(MIN_STEPS_PER_LIFETIME / life)))
		if f < fps:
			if cpu:
				_pending.append([node, node.fixed_fps, 0])
				node.fixed_fps = f
				after = ceili(pre * f)
			elif life >= GPU_MIN_LIFETIME:
				node.fixed_fps = f
				node.interpolate = true
				after = ceili(pre * f)
		if not cpu and after > MAX_STEPS and not node.one_shot:
			_warming.append([node, pre, node.preprocess, 0])
			node.preprocess = 0.0
			after = 0
	if after < before:
		_tuned += 1
		_steps_before += before
		_steps_after += after


# CPUParticles2D pre-process in their first update while emitting and visible (internal process or draw); restore
# their own fixed_fps once that has surely happened.
func _after_draw() -> void:
	var i := _pending.size() - 1
	while i >= 0:
		var entry: Array = _pending[i]
		var node: CPUParticles2D = entry[0] if is_instance_valid(entry[0]) else null
		if node == null:
			_pending.remove_at(i)
		elif node.emitting and node.is_visible_in_tree() and node.can_process():
			entry[2] += 1
			if entry[2] >= 2:
				node.fixed_fps = entry[1]
				_pending.remove_at(i)
		i -= 1
	_warm_up()
	if _tuned > 0:
		print("[PORT] particles: shortened pre-process of %d effects, %d -> %d steps (%d fast-forwarding over the next frames)" % [_tuned, _steps_before, _steps_after, _warming.size()])
		_tuned = 0
		_steps_before = 0
		_steps_after = 0


# Feeds the deferred pre-process of GPU effects (rule 3) in per-frame chunks, once each has had its first update.
func _warm_up() -> void:
	var budget := GPU_STEPS_PER_FRAME
	var i := 0
	while i < _warming.size():
		var entry: Array = _warming[i]
		var node: GPUParticles2D = entry[0] if is_instance_valid(entry[0]) else null
		if node == null:
			_warming.remove_at(i)
			continue
		if not (node.emitting and node.is_visible_in_tree() and node.can_process()):
			i += 1
			continue
		entry[3] += 1
		if entry[3] < 2 or budget <= 0:
			i += 1
			continue
		var fps: int = node.fixed_fps if node.fixed_fps > 0 else 30
		var steps := mini(ceili(entry[1] * fps), budget)
		var seconds := steps / float(fps)
		node.request_particles_process(seconds)
		budget -= steps
		entry[1] -= seconds
		if entry[1] <= 0.0001:
			node.preprocess = entry[2] # only used again if the effect restarts
			_warming.remove_at(i)
		else:
			i += 1
