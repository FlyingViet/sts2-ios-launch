using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;

// iOS port frame-time monitor: logs hitches (> HitchMs) with GC/pipeline context and a summary every 15 s.
static unsafe class PortPerf
{
	const double HitchMs = 50;
	const double SummarySec = 15;
	static readonly Stopwatch Clock = Stopwatch.StartNew();
	static double _lastMs, _windowStartMs;
	static readonly List<double> _frames = new(4096);
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
		GD.Print("[PERF] refresh=", DisplayServer.ScreenGetRefreshRate(), " maxFps=", Engine.MaxFps,
			" vsync=", DisplayServer.WindowGetVsyncMode(), " physicsTps=", Engine.PhysicsTicksPerSecond,
			" serverGC=", GCSettings.IsServerGC, " latency=", GCSettings.LatencyMode,
			" concurrent=", AppContext.TryGetSwitch("System.GC.Concurrent", out bool c) ? c.ToString() : "default");
		_g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2);
		_pause = GC.GetTotalPauseDuration(); _pipe = Pipelines();
		_lastMs = _windowStartMs = Clock.Elapsed.TotalMilliseconds;
		tree.ProcessFrame += OnFrame;
	}

	static void OnFrame()
	{
		double now = Clock.Elapsed.TotalMilliseconds, dt = now - _lastMs;
		_lastMs = now;
		_frames.Add(dt);
		int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
		TimeSpan pause = GC.GetTotalPauseDuration();
		double pipe = Pipelines();
		if (dt > HitchMs && _hitchLogs++ < 300)
		{
			GD.Print($"[PERF] hitch t={now / 1000:F1}s dt={dt:F0}ms gc={g0 - _g0}/{g1 - _g1}/{g2 - _g2} gcPause={(pause - _pause).TotalMilliseconds:F1}ms " +
				$"pipelines+={pipe - _pipe:F0} process={Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F1}ms " +
				$"draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):F0} objs={Performance.GetMonitor(Performance.Monitor.ObjectCount):F0}");
		}
		_wg0 += g0 - _g0; _wg1 += g1 - _g1; _wg2 += g2 - _g2; _wPause += pause - _pause; _wPipe += pipe - _pipe;
		_g0 = g0; _g1 = g1; _g2 = g2; _pause = pause; _pipe = pipe;
		if (now - _windowStartMs >= SummarySec * 1000)
		{
			_frames.Sort();
			int n = _frames.Count;
			double P(double q) => _frames[Math.Min(n - 1, (int)(q * n))];
			int over(double ms) { int k = 0; foreach (var f in _frames) if (f > ms) k++; return k; }
			GD.Print($"[PERF] summary t={now / 1000:F0}s fps={n / ((now - _windowStartMs) / 1000):F0} p50={P(0.5):F1} p95={P(0.95):F1} p99={P(0.99):F1} max={_frames[n - 1]:F0}ms " +
				$">12ms={over(12)} >25ms={over(25)} >50ms={over(50)} gc={_wg0}/{_wg1}/{_wg2} gcPause={_wPause.TotalMilliseconds:F0}ms pipelines={_wPipe:F0} " +
				$"heap={GC.GetTotalMemory(false) / 1048576}MB");
			_frames.Clear(); _windowStartMs = now; _wg0 = _wg1 = _wg2 = 0; _wPause = TimeSpan.Zero; _wPipe = 0;
		}
	}
}
