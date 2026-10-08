#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using Environment = System.Environment;

namespace PortMultiplayer;

// Keeps a multiplayer game running while this device isn't in front.
//
// Godot's iOS layer stops its display link whenever the app loses focus (Notification or Control Center, a banner,
// the app switcher), and that display link drives the whole game loop, networking included; in the background iOS
// then suspends the app within a fraction of a second. Meanwhile everyone else's game waits for this device, and
// eventually drops it. So during a multiplayer session (res://port/app_state.gd relays the app notifications):
//  - focus lost: start a silent background-audio session that mixes with other audio (Info.plist
//    UIBackgroundModes: audio) right away, as iOS only lets an app keep running in the background if it's already
//    playing; and restart Godot's display link (rendering is allowed while still on screen);
//  - in the background, where display links don't run: run game frames from a timer with rendering off (iOS doesn't
//    allow GPU work in the background);
//  - back in front: stop the audio (the game follows the silent switch again) and render as usual.
// Outside multiplayer Godot keeps pausing on focus loss as usual.
static class PortKeepAlive
{
	// True while a multiplayer session is connected (updated every frame).
	public static volatile bool SessionActive;

	const int BackgroundFrameMs = 33;
	static Func<bool>? _isConnected;
	static long _trackedAt, _lastFrame, _lastRestart;
	static bool _everConnected;
	static volatile int _appState; // UIApplicationState: 0 active, 1 inactive, 2 background
	static volatile bool _background, _framePending;
	static IntPtr _view;
	static System.Threading.Timer? _watchdog;
	static IntPtr _player, _prevCategory;
	static nuint _prevOptions;

	static void Log(string msg) => GD.Print("[MP] " + msg);
	static long Now => Environment.TickCount64;

	public static void Install()
	{
		var tree = (SceneTree)Engine.GetMainLoop();
		tree.ProcessFrame += OnFrame;
		if (tree.Root.GetNodeOrNull("PortAppState") is { } relay)
		{
			relay.Connect("focus_lost", Callable.From(OnFocusLost));
			relay.Connect("focus_regained", Callable.From(OnFocusRegained));
			relay.Connect("entered_background", Callable.From(OnEnteredBackground));
			relay.Connect("left_background", Callable.From(OnLeftBackground));
		}
		else
			Log("PortAppState autoload missing: multiplayer pauses when the app isn't in front");
		Interlocked.Exchange(ref _lastFrame, Now);
		_watchdog = new System.Threading.Timer(_ => Watch(), null, 25, 25);
	}

	// Main thread. A session that hasn't connected yet counts as active for 30 s.
	public static void Track(Func<bool> isConnected)
	{
		_isConnected = isConnected;
		_trackedAt = Now;
		_everConnected = false;
		SessionActive = true;
	}

	static void OnFrame()
	{
		Interlocked.Exchange(ref _lastFrame, Now);
		_framePending = false;
		if (_view == IntPtr.Zero) _view = ObjC.GodotView();
		_appState = (int)ObjC.ApplicationState();
		bool active = false;
		if (_isConnected is { } isConnected)
		{
			bool connected = isConnected();
			_everConnected |= connected;
			active = connected || (!_everConnected && Now - _trackedAt < 30_000);
			if (!active)
			{
				_isConnected = null;
				Log("multiplayer session ended");
			}
		}
		SessionActive = active;
		if (_background)
		{
			if (active)
				RenderingServer.RenderLoopEnabled = false;
			else
			{
				// Nothing to keep running: let the app suspend; Godot restarts rendering when it comes back.
				_background = false;
				Native.Send(_view, "stopRendering");
				StopAudio();
				Log("multiplayer ended in the background: pausing as usual");
			}
		}
	}

	// ---- app notifications (main thread, from inside the UIKit callbacks) ----

	// applicationWillResignActive: Godot stops its display link right after this returns.
	static void OnFocusLost()
	{
		if (!SessionActive)
			return;
		if (_view == IntPtr.Zero) _view = ObjC.GodotView();
		StartAudio();
		Native.PerformOnMain(_view, "startRendering"); // runs after Godot's stopRendering
		Log("app lost focus during multiplayer: keeping the game running");
	}

	// applicationDidEnterBackground.
	static void OnEnteredBackground()
	{
		if (!SessionActive)
			return;
		StartAudio();
		_background = true;
		RenderingServer.RenderLoopEnabled = false;
		Log("in the background during multiplayer: running without rendering");
	}

	// applicationWillEnterForeground (also sends focus_regained).
	static void OnLeftBackground()
	{
		if (!_background)
			return;
		_background = false;
		// The display link made in the background may never fire: replace it.
		Native.Send(_view, "stopRendering");
		Native.Send(_view, "startRendering");
		Log("back in front: rendering again");
	}

	static void OnFocusRegained()
	{
		OnLeftBackground();
		RenderingServer.RenderLoopEnabled = true;
		StopAudio();
	}

	// Watchdog thread, every 25 ms: runs frames in the background, and restarts the display link if it stopped
	// while on screen anyway.
	static void Watch()
	{
		if (!SessionActive || _view == IntPtr.Zero)
			return;
		long since = Now - Interlocked.Read(ref _lastFrame);
		if (_background)
		{
			if (since >= BackgroundFrameMs && (!_framePending || since > 500))
			{
				if (_framePending) Native.PerformOnMain(_view, "startRendering"); // drawView skips while stopped
				_framePending = true;
				Native.PerformOnMain(_view, "drawView");
			}
		}
		else if (since > 300 && _appState != 2 && Now - _lastRestart > 500)
		{
			_lastRestart = Now;
			Native.PerformOnMain(_view, "startRendering");
		}
	}

	// ---- silent background audio ----

	static void StartAudio()
	{
		if (_prevCategory != IntPtr.Zero) // already on
			return;
		IntPtr pool = Native.objc_autoreleasePoolPush();
		try
		{
			IntPtr session = Native.Send(Native.Class("AVAudioSession"), "sharedInstance");
			_prevCategory = Native.Send(Native.Send(session, "category"), "retain");
			_prevOptions = Native.SendNuint(session, "categoryOptions");
			// Playback keeps running in the background; MixWithOthers leaves the user's own audio alone.
			Native.SetCategory(session, Native.NSString("AVAudioSessionCategoryPlayback"), 1);
			Native.SetActive(session, true);
			IntPtr playerClass = Native.Class("AVAudioPlayer");
			if (playerClass == IntPtr.Zero)
			{
				Log("AVAudioPlayer unavailable; the background session may be suspended");
				return;
			}
			IntPtr url = Native.Send(Native.Class("NSURL"), "fileURLWithPath:", Native.NSString(SilenceFile()));
			_player = Native.InitWithUrl(Native.Send(playerClass, "alloc"), url);
			if (_player == IntPtr.Zero)
				return;
			Native.SendNint(_player, "setNumberOfLoops:", -1);
			Native.Send(_player, "play");
		}
		catch (Exception e) { Log("background audio failed: " + e.Message); }
		finally { Native.objc_autoreleasePoolPop(pool); }
	}

	static void StopAudio()
	{
		IntPtr pool = Native.objc_autoreleasePoolPush();
		try
		{
			if (_player != IntPtr.Zero)
			{
				Native.Send(_player, "stop");
				Native.Send(_player, "release");
				_player = IntPtr.Zero;
			}
			if (_prevCategory != IntPtr.Zero)
			{
				Native.SetCategory(Native.Send(Native.Class("AVAudioSession"), "sharedInstance"), _prevCategory, _prevOptions);
				Native.Send(_prevCategory, "release");
				_prevCategory = IntPtr.Zero;
			}
		}
		catch (Exception e) { Log("restoring audio failed: " + e.Message); }
		finally { Native.objc_autoreleasePoolPop(pool); }
	}

	// One second of 8 kHz 16-bit mono silence, looped.
	static string SilenceFile()
	{
		string path = Path.Combine(Path.GetTempPath(), "port_silence.wav");
		if (File.Exists(path))
			return path;
		const int rate = 8000, bytes = rate * 2;
		using var w = new BinaryWriter(File.Create(path));
		w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
		w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
		w.Write((short)2); w.Write((short)16);
		w.Write("data"u8); w.Write(bytes); w.Write(new byte[bytes]);
		return path;
	}

	static class Native
	{
		const string Lib = "/usr/lib/libobjc.A.dylib";
		[DllImport(Lib)] static extern IntPtr objc_getClass(string name);
		[DllImport(Lib)] static extern IntPtr sel_registerName(string name);
		[DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
		[DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);
		[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr r, IntPtr s);
		[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a);
		[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
		[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern nuint MsgNuint(IntPtr r, IntPtr s);
		[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern void MsgNint(IntPtr r, IntPtr s, nint a);
		[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern byte MsgCategory(IntPtr r, IntPtr s, IntPtr category, nuint options, IntPtr error);
		[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern byte MsgActive(IntPtr r, IntPtr s, byte active, IntPtr error);
		[DllImport(Lib, EntryPoint = "objc_msgSend")] static extern void MsgPerform(IntPtr r, IntPtr s, IntPtr selector, IntPtr arg, byte wait);

		public static IntPtr Class(string name) => objc_getClass(name);
		public static IntPtr Send(IntPtr r, string sel) => Msg(r, sel_registerName(sel));
		public static IntPtr Send(IntPtr r, string sel, IntPtr a) => Msg(r, sel_registerName(sel), a);
		public static nuint SendNuint(IntPtr r, string sel) => MsgNuint(r, sel_registerName(sel));
		public static void SendNint(IntPtr r, string sel, nint a) => MsgNint(r, sel_registerName(sel), a);
		public static void SetCategory(IntPtr session, IntPtr category, nuint options) =>
			MsgCategory(session, sel_registerName("setCategory:withOptions:error:"), category, options, IntPtr.Zero);
		public static void SetActive(IntPtr session, bool active) =>
			MsgActive(session, sel_registerName("setActive:error:"), active ? (byte)1 : (byte)0, IntPtr.Zero);
		public static IntPtr InitWithUrl(IntPtr player, IntPtr url) =>
			Msg(player, sel_registerName("initWithContentsOfURL:error:"), url, IntPtr.Zero);
		public static void PerformOnMain(IntPtr target, string sel) =>
			MsgPerform(target, sel_registerName("performSelectorOnMainThread:withObject:waitUntilDone:"), sel_registerName(sel), IntPtr.Zero, 0);

		// Autoreleased; callers hold an autorelease pool.
		public static IntPtr NSString(string s)
		{
			IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(s);
			try { return Msg(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8); }
			finally { Marshal.FreeCoTaskMem(utf8); }
		}
	}
}
