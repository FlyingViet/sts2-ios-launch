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
static class PortFrameRate
{
	public static bool Pacing = true; // false = Godot's stock behaviour (for the benchmark)
	public static int DisplayRate { get; private set; }

	static int _limit;
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
		Update();
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
		if (link == _link && !_dirty)
			return;
		int refresh = Math.Max(60, (int)Math.Round(DisplayServer.ScreenGetRefreshRate()));
		int rate = Pacing ? RateFor(_limit, refresh) : refresh;
		if (link != IntPtr.Zero)
			ObjC.SetPreferredFrameRate(link, rate);
		Engine.PortSetMaxFpsNative(Pacing ? 0 : _limit);
		_link = link;
		_dirty = false;
		DisplayRate = rate;
		string log = $"[PORT] frame rate: game limit {_limit}, display link {rate} Hz (refresh {refresh}, pacing {(Pacing ? "on" : "off")})";
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
