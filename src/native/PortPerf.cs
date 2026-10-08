#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;

// iOS port frame-time monitor: logs hitches (> HitchMs) with GC/pipeline context and a summary every 15 s.
// Per frame it also times game logic (process_frame -> frame_pre_draw) and rendering (frame_pre_draw ->
// frame_post_draw, which includes waiting for a free drawable when the GPU is behind; Metal has no GPU timers here).
// Benchmark: push a file named port_bench to the app's Documents (devicectl device copy to) and launch; on the main
// menu it cycles frame pacing / FPS limit / MSAA settings and logs a [BENCH] line per setting, then removes the file.
static unsafe class PortPerf
{
	const double HitchMs = 50;
	const double SummarySec = 15;
	static readonly Stopwatch Clock = Stopwatch.StartNew();
	static double _lastMs, _windowStartMs, _preDrawMs, _logicMs, _renderMs;
	static readonly List<double> _frames = new(4096), _logic = new(4096), _render = new(4096);
	static int _g0, _g1, _g2, _wg0, _wg1, _wg2, _hitchLogs;
	static TimeSpan _pause, _wPause;
	static double _pipe, _wPipe;

	// Starting a Thread here deadlocks (runtime init is still in progress), so defer via GCD on the main
	// queue, which is also Godot's main thread on iOS.
	[DllImport("/usr/lib/libSystem.B.dylib")] static extern ulong dispatch_time(ulong when, long deltaNs);
	[DllImport("/usr/lib/libSystem.B.dylib")] static extern void dispatch_after_f(ulong when, IntPtr queue, IntPtr ctx, delegate* unmanaged<IntPtr, void> work);
	static IntPtr _mainQueue;

	[ModuleInitializer]
	internal static void Init()
	{
		_mainQueue = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_dispatch_main_q");
		Schedule(3);
	}

	static void Schedule(int seconds) => dispatch_after_f(dispatch_time(0, seconds * 1_000_000_000L), _mainQueue, IntPtr.Zero, &OnMainQueue);

	[UnmanagedCallersOnly]
	static void OnMainQueue(IntPtr ctx)
	{
		try
		{
			if (Engine.GetMainLoop() is SceneTree) Setup();
			else Schedule(2);
		}
		catch (Exception e) { Console.WriteLine("[PERF] setup failed " + e); }
	}

	static double Pipelines() =>
		Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas) +
		Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh) +
		Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) +
		Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw) +
		Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization);

	static void Setup()
	{
		var tree = (SceneTree)Engine.GetMainLoop();
		PortFrameRate.Install();
		GD.Print("[PERF] refresh=", DisplayServer.ScreenGetRefreshRate(), " maxFps=", Engine.MaxFps,
			" vsync=", DisplayServer.WindowGetVsyncMode(), " physicsTps=", Engine.PhysicsTicksPerSecond,
			" serverGC=", GCSettings.IsServerGC, " latency=", GCSettings.LatencyMode,
			" concurrent=", AppContext.TryGetSwitch("System.GC.Concurrent", out bool c) ? c.ToString() : "default");
		GD.Print("[PORT] FMOD file system: ", FmodFileSystemStatus());
		var rd = RenderingServer.GetRenderingDevice();
		string Fmt(RenderingDevice.DataFormat f) => rd != null && rd.TextureIsFormatSupportedForUsage(f, RenderingDevice.TextureUsageBits.SamplingBit) ? "yes" : "no";
		GD.Print("[PERF] gpu=", rd?.GetDeviceName(), " bc1=", Fmt(RenderingDevice.DataFormat.Bc1RgbaUnormBlock),
			" bc3=", Fmt(RenderingDevice.DataFormat.Bc3UnormBlock), " bc7=", Fmt(RenderingDevice.DataFormat.Bc7UnormBlock),
			" astc4x4=", Fmt(RenderingDevice.DataFormat.Astc4X4UnormBlock), " msaa2d=", tree.Root.Msaa2D,
			" thermal=", ObjC.ThermalState(), " lowPower=", ObjC.LowPowerMode());
		RenderingServer.FramePreDraw += () =>
		{
			double t = Clock.Elapsed.TotalMilliseconds;
			_logicMs = t - _lastMs;
			_preDrawMs = t;
		};
		RenderingServer.FramePostDraw += () => _renderMs = Clock.Elapsed.TotalMilliseconds - _preDrawMs;
		if (FileAccess.FileExists(BenchFlag))
		{
			_bench = BenchPlan(FileAccess.GetFileAsString(BenchFlag));
			PortFrameRate.Adaptive = false; // measure fixed rates
			DirAccess.RemoveAbsolute(BenchFlag);
			_benchNextMs = Clock.Elapsed.TotalMilliseconds + BenchDelaySec * 1000;
			GD.Print($"[BENCH] {_bench.Count} settings x {BenchSec}s, starting in {BenchDelaySec}s (leave the app on the main menu)");
		}
		_g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2);
		_pause = GC.GetTotalPauseDuration(); _pipe = Pipelines();
		_lastMs = _windowStartMs = Clock.Elapsed.TotalMilliseconds;
		tree.ProcessFrame += OnFrame;
	}

	// Set by load_all_fmod_plugins in src/ios/fmod_plugins_stub.cpp (compiled into the app executable).
	static string FmodFileSystemStatus()
	{
		if (!NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "port_fmod_file_system_status", out IntPtr fn))
			return "unknown (status export missing)";
		return Marshal.PtrToStringUTF8(((delegate* unmanaged<IntPtr>)fn)()) ?? "unknown";
	}

	static void OnFrame()
	{
		double now = Clock.Elapsed.TotalMilliseconds, dt = now - _lastMs;
		_lastMs = now;
		PortFrameRate.Update();
		_frames.Add(dt);
		_logic.Add(_logicMs);
		_render.Add(_renderMs);
		if (_bench != null)
			BenchStep(now, dt);
		int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
		TimeSpan pause = GC.GetTotalPauseDuration();
		double pipe = Pipelines();
		if (dt > HitchMs && _hitchLogs++ < 300)
		{
			GD.Print($"[PERF] hitch t={now / 1000:F1}s dt={dt:F0}ms gc={g0 - _g0}/{g1 - _g1}/{g2 - _g2} gcPause={(pause - _pause).TotalMilliseconds:F1}ms " +
				$"pipelines+={pipe - _pipe:F0} logic={_logicMs:F1}ms render={_renderMs:F1}ms " +
				$"draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):F0} objs={Performance.GetMonitor(Performance.Monitor.ObjectCount):F0}");
		}
		_wg0 += g0 - _g0; _wg1 += g1 - _g1; _wg2 += g2 - _g2; _wPause += pause - _pause; _wPipe += pipe - _pipe;
		_g0 = g0; _g1 = g1; _g2 = g2; _pause = pause; _pipe = pipe;
		if (now - _windowStartMs >= SummarySec * 1000)
		{
			int n = _frames.Count;
			GD.Print($"[PERF] summary t={now / 1000:F0}s fps={n / ((now - _windowStartMs) / 1000):F0} {Stats(_frames, _logic, _render)} " +
				$"gc={_wg0}/{_wg1}/{_wg2} gcPause={_wPause.TotalMilliseconds:F0}ms pipelines={_wPipe:F0} heap={GC.GetTotalMemory(false) / 1048576}MB " +
				$"vram={RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed) / 1048576}MB display={PortFrameRate.DisplayRate}Hz " +
				$"idle={100 * PortFrameRate.IdleFrames / Math.Max(1, PortFrameRate.IdleFrames + PortFrameRate.FullFrames)}% " +
				$"busy(touch/tween)={PortFrameRate.TouchBusy}/{PortFrameRate.TweenBusy} tweens<={PortFrameRate.MaxTweens} oldest={PortFrameRate.OldestTween:F0}s " +
				$"thermal={ObjC.ThermalState()}{(ObjC.LowPowerMode() ? " lowPower" : "")}");
			_frames.Clear(); _logic.Clear(); _render.Clear();
			PortFrameRate.IdleFrames = PortFrameRate.FullFrames = 0;
			PortFrameRate.TouchBusy = PortFrameRate.TweenBusy = PortFrameRate.MaxTweens = 0;
			PortFrameRate.OldestTween = 0;
			_windowStartMs = now; _wg0 = _wg1 = _wg2 = 0; _wPause = TimeSpan.Zero; _wPipe = 0;
		}
	}

	static string Stats(List<double> frames, List<double> logic, List<double> render)
	{
		static double P(List<double> v, double q) => v[Math.Min(v.Count - 1, (int)(q * v.Count))];
		int n = frames.Count;
		if (n == 0)
			return "no frames";
		int over20 = 0, over25 = 0, over50 = 0;
		foreach (var f in frames)
		{
			over20 += f > 20 ? 1 : 0;
			over25 += f > 25 ? 1 : 0;
			over50 += f > 50 ? 1 : 0;
		}
		var fs = new List<double>(frames); fs.Sort();
		var ls = new List<double>(logic); ls.Sort();
		var rs = new List<double>(render); rs.Sort();
		return $"frame p50={P(fs, 0.5):F1} p95={P(fs, 0.95):F1} p99={P(fs, 0.99):F1} max={fs[n - 1]:F0}ms >20ms={over20} >25ms={over25} >50ms={over50} " +
			$"logic p50={P(ls, 0.5):F1} p95={P(ls, 0.95):F1} render p50={P(rs, 0.5):F1} p95={P(rs, 0.95):F1}";
	}

	// ---- benchmark (see top) ----
	const string BenchFlag = "user://port_bench";
	const double BenchDelaySec = 15, BenchSec = 20, BenchSettleSec = 2;
	static List<(bool Pacing, int Fps, int Msaa)>? _bench;
	static int _benchIndex = -1, _benchOrigFps;
	static Viewport.Msaa _benchOrigMsaa;
	static double _benchNextMs, _benchStartMs;
	static readonly List<double> _bFrames = new(4096), _bLogic = new(4096), _bRender = new(4096);

	// One setting per line: "pacing=on fps=60 msaa=2". Empty file = default plan.
	static List<(bool, int, int)> BenchPlan(string text)
	{
		var plan = new List<(bool, int, int)>();
		foreach (var raw in text.Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#'))
				continue;
			bool pacing = true; int fps = 60, msaa = 2;
			foreach (var kv in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				var p = kv.Split('=');
				if (p.Length != 2) continue;
				if (p[0] == "pacing") pacing = p[1] == "on";
				else if (p[0] == "fps") fps = int.Parse(p[1]);
				else if (p[0] == "msaa") msaa = int.Parse(p[1]);
			}
			plan.Add((pacing, fps, msaa));
		}
		if (plan.Count == 0)
			plan.AddRange(new[] { (false, 60, 2), (true, 60, 2), (true, 60, 0), (true, 120, 2), (true, 120, 0), (false, 60, 2) });
		return plan;
	}

	static void BenchStep(double now, double dt)
	{
		var root = ((SceneTree)Engine.GetMainLoop()).Root;
		if (_benchIndex >= 0 && now - _benchStartMs >= BenchSettleSec * 1000)
		{
			_bFrames.Add(dt); _bLogic.Add(_logicMs); _bRender.Add(_renderMs);
		}
		if (now < _benchNextMs)
			return;
		if (_benchIndex >= 0)
		{
			var c = _bench![_benchIndex];
			GD.Print($"[BENCH] pacing={(c.Pacing ? "on" : "off")} fps={c.Fps} msaa={c.Msaa}x: fps={_bFrames.Count / (BenchSec - BenchSettleSec):F1} " +
				$"{Stats(_bFrames, _bLogic, _bRender)} thermal={ObjC.ThermalState()}");
		}
		else
		{
			_benchOrigFps = Engine.MaxFps;
			_benchOrigMsaa = root.Msaa2D;
		}
		_bFrames.Clear(); _bLogic.Clear(); _bRender.Clear();
		if (++_benchIndex >= _bench!.Count)
		{
			PortFrameRate.Pacing = true;
			Engine.MaxFps = _benchOrigFps;
			root.Msaa2D = _benchOrigMsaa;
			PortFrameRate.Refresh();
			GD.Print("[BENCH] done");
			_bench = null;
			return;
		}
		var next = _bench[_benchIndex];
		PortFrameRate.Pacing = next.Pacing;
		Engine.MaxFps = next.Fps;
		root.Msaa2D = next.Msaa switch { 2 => Viewport.Msaa.Msaa2X, 4 => Viewport.Msaa.Msaa4X, 8 => Viewport.Msaa.Msaa8X, _ => Viewport.Msaa.Disabled };
		PortFrameRate.Refresh();
		_benchStartMs = now;
		_benchNextMs = now + BenchSec * 1000;
	}
}
