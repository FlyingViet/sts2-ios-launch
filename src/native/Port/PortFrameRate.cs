#nullable enable
using System;
using System.Runtime.InteropServices;
using Godot;

// iOS frame pacing for the game's FPS limit (Settings > FPS, default 60).
//
// Godot's iOS view asks the display for 120 Hz callbacks and runs a game frame on each. Metal enforces Engine.MaxFps
// by holding each frame back at presentation (presentDrawable:afterMinimumDuration:) until its drawables run out, so
// game frames start 8, 17 or 25 ms apart while the screen shows them at a steady 60 Hz: animation judders and queued
// frames add input latency. Instead, run the display link at the limit (the largest rate dividing the refresh rate
// that doesn't exceed it) and leave Metal's own limit off. The patched GodotSharp Engine.MaxFps keeps returning the
// game's value and reports changes (settings, the 30 FPS background limit) through Engine.PortMaxFpsHook.
//
// Adaptive rate (battery and heat): above 60, the full rate only runs while something is happening: a finger on the
// screen or touched in the last moments, or a one-shot tween running (the game animates cards, hits, numbers and
// screen changes with tweens, all well under 2.5 s; looping tweens, tweens older than 2.5 s and tweens whose clock
// hasn't moved for 1.5 s, like one that sits on the main menu, don't count). After 2 s of calm it drops to 60 Hz.
static class PortFrameRate
{
	public static bool Pacing = true; // false = Godot's stock behaviour (for the benchmark)
	public static bool Adaptive = true;
	public static int DisplayRate { get; private set; }
	public static int IdleFrames, FullFrames; // frames at the reduced / full rate since the last PERF summary
	public static int TouchBusy, TweenBusy, MaxTweens; // why frames were busy (diagnostics, PERF summary)
	public static double OldestTween; // seconds

	const double TouchHoldMs = 1500, IdleAfterMs = 2000, AmbientTweenSec = 2.5, StuckTweenMs = 1500;
	static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
	static double _lastInputMs, _lastBusyMs;
	static int _touches, _frame;
	static bool _tweening;

	static int _limit, _refresh;
	static readonly System.Collections.Generic.Dictionary<ulong, (double Age, double Since)> _progress = new();
	static bool _dirty = true;
	static IntPtr _link;
	static string _lastLog = "";

	internal static void Install()
	{
		_limit = Engine.PortHasMaxFpsRequest ? Engine.PortRequestedMaxFps : Engine.PortGetMaxFpsNative();
		Engine.PortMaxFpsHook = limit =>
		{
			_limit = limit;
			_dirty = true;
			Update();
		};
		((SceneTree)Engine.GetMainLoop()).Root.WindowInput += OnInput;
		Update();
	}

	static void OnInput(InputEvent e)
	{
		_lastInputMs = Clock.Elapsed.TotalMilliseconds;
		if (e is InputEventScreenTouch touch)
			_touches = Math.Max(0, _touches + (touch.Pressed ? 1 : -1));
	}

	// Main thread: any running tween that will end (a moving card, a hit, a screen transition)?
	static bool Tweening()
	{
		int n = 0;
		foreach (var tween in ((SceneTree)Engine.GetMainLoop()).GetProcessedTweens())
		{
			if (!tween.IsRunning())
				continue;
			if (tween.GetLoopsLeft() == -1)
				continue;
			double age = tween.GetTotalElapsedTime();
			// A tween whose clock doesn't move (empty, or waiting on something) animates nothing: e.g. one that sits on
			// the main menu at 0 s forever. Count it only until it has been seen stuck for a while.
			ulong id = tween.GetInstanceId();
			double now = Clock.Elapsed.TotalMilliseconds;
			if (!_progress.TryGetValue(id, out var seen) || seen.Age != age)
				_progress[id] = (age, now);
			else if (now - seen.Since > StuckTweenMs)
				continue;
			OldestTween = Math.Max(OldestTween, age);
			if (age < AmbientTweenSec)
				n++;
		}
		MaxTweens = Math.Max(MaxTweens, n);
		if (_progress.Count > 256) _progress.Clear(); // finished tweens; live ones re-register next check
		return n > 0;
	}

	public static void Refresh()
	{
		_dirty = true;
		Update();
	}

	// Every frame (main thread): Godot replaces the display link when the app returns from the background.
	internal static void Update()
	{
		IntPtr link = ObjC.GodotDisplayLink();
		if (_refresh == 0 || _dirty)
			_refresh = Math.Max(60, (int)Math.Round(DisplayServer.ScreenGetRefreshRate()));
		int full = Pacing ? RateFor(_limit, _refresh) : _refresh;
		int rate = full;
		if (Adaptive && Pacing && full > 60)
		{
			double now = Clock.Elapsed.TotalMilliseconds;
			if ((_frame++ & 1) == 0) _tweening = Tweening(); // every other frame is plenty
			bool touch = _touches > 0 || now - _lastInputMs < TouchHoldMs;
			if (touch) TouchBusy++;
			if (_tweening) TweenBusy++;
			if (touch || _tweening)
				_lastBusyMs = now;
			if (now - _lastBusyMs > IdleAfterMs)
				rate = RateFor(60, _refresh);
		}
		if (rate < full) IdleFrames++; else FullFrames++;
		if (link == _link && !_dirty && rate == DisplayRate)
			return;
		if (link != IntPtr.Zero)
			ObjC.SetPreferredFrameRate(link, rate);
		if (link == _link && !_dirty)
		{
			DisplayRate = rate; // adaptive switch only: counted in the PERF summary, not logged
			return;
		}
		Engine.PortSetMaxFpsNative(Pacing ? 0 : _limit);
		_link = link;
		_dirty = false;
		DisplayRate = rate;
		int refresh = _refresh;
		string log = $"[PORT] frame rate: game limit {_limit}, display link {full} Hz{(Adaptive && full > 60 ? " (60 when idle)" : "")} (refresh {refresh}, pacing {(Pacing ? "on" : "off")})";
		if (log != _lastLog)
		{
			_lastLog = log;
			GD.Print(log);
		}
	}

	// Largest refresh/n not above the limit (59 counts as 60): 60 -> 60, 90 -> 60, 30 -> 30, 0 or >= refresh -> refresh.
	static int RateFor(int limit, int refresh)
	{
		if (limit <= 0 || limit >= refresh)
			return refresh;
		for (int n = 1; n <= refresh; n++)
			if (refresh % n == 0 && refresh / n <= limit + 1)
				return refresh / n;
		return refresh;
	}
}

static class ObjC
{
	const string Lib = "/usr/lib/libobjc.A.dylib";
	[DllImport(Lib)] static extern IntPtr objc_getClass(string name);
	[DllImport(Lib)] static extern IntPtr sel_registerName(string name);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, nuint index);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern nint SendNint(IntPtr receiver, IntPtr selector);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern byte SendBool(IntPtr receiver, IntPtr selector);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr arg);
	// CAFrameRateRange {float minimum, maximum, preferred} is passed like three float arguments on arm64.
	[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern void SendRange(IntPtr receiver, IntPtr selector, float min, float max, float preferred);

	static IntPtr Sel(string name) => sel_registerName(name);
	static readonly IntPtr SelDisplayLink = Sel("displayLink");
	static readonly IntPtr SelRespondsTo = Sel("respondsToSelector:");
	static readonly IntPtr SelSetRange = Sel("setPreferredFrameRateRange:");
	static IntPtr _view;

	// GDTView (drivers/apple_embedded/godot_view_apple_embedded.mm) owns the CADisplayLink that drives the game loop.
	public static IntPtr GodotDisplayLink()
	{
		IntPtr view = GodotView();
		return view == IntPtr.Zero ? IntPtr.Zero : Send(view, SelDisplayLink);
	}

	// Main thread.
	public static IntPtr GodotView()
	{
		if (_view == IntPtr.Zero)
		{
			IntPtr app = Send(objc_getClass("UIApplication"), Sel("sharedApplication"));
			IntPtr window = Send(Send(app, Sel("delegate")), Sel("window"));
			_view = FindView(Send(Send(window, Sel("rootViewController")), Sel("view")), 0);
		}
		return _view;
	}

	static IntPtr FindView(IntPtr view, int depth)
	{
		if (view == IntPtr.Zero || depth > 4)
			return IntPtr.Zero;
		if (SendBool(view, SelRespondsTo, SelDisplayLink) != 0)
			return view;
		IntPtr subviews = Send(view, Sel("subviews"));
		nint count = SendNint(subviews, Sel("count"));
		for (nint i = 0; i < count; i++)
		{
			IntPtr found = FindView(Send(subviews, Sel("objectAtIndex:"), (nuint)i), depth + 1);
			if (found != IntPtr.Zero)
				return found;
		}
		return IntPtr.Zero;
	}

	public static void SetPreferredFrameRate(IntPtr displayLink, int rate) => SendRange(displayLink, SelSetRange, rate, rate, rate);

	// UIApplicationState (main thread): 0 active, 1 inactive, 2 background.
	public static nint ApplicationState() => SendNint(Send(objc_getClass("UIApplication"), Sel("sharedApplication")), Sel("applicationState"));

	static IntPtr ProcessInfo() => Send(objc_getClass("NSProcessInfo"), Sel("processInfo"));

	// NSProcessInfoThermalState: 0 nominal, 1 fair, 2 serious, 3 critical.
	public static int ThermalState() => (int)SendNint(ProcessInfo(), Sel("thermalState"));

	public static bool LowPowerMode() => SendBool(ProcessInfo(), Sel("isLowPowerModeEnabled")) != 0;
}
