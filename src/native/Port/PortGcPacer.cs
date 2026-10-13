#nullable enable
using System;
using System.Diagnostics;
using System.Runtime;
using Godot;
using MegaCrit.Sts2.Core.Runs;

// iOS port: moves .NET garbage collections to calm moments.
//
// The game allocates steadily (LINQ, async continuations, card and VFX nodes), so the GC runs every few seconds of play
// and each gen0/gen1 collection stops the main thread for 20-40 ms on an iPhone (handle tables of ~17k Godot objects,
// finalizer scan, card marking): several dropped frames, often in the middle of a card or hit animation.
//
// At a calm moment (no finger down, and the game waiting for the player: its action queue empty and idle; outside a
// run, no one-shot animation), collect now, then open a no-GC region with a large allocation budget so the GC stays out
// of the way until the next calm moment. If the game allocates the whole budget before that, the runtime ends the region
// with an ordinary collection, as before. (In combat some short animation runs in ~95% of frames, so "nothing animating"
// alone is too rare; the game's actions wait for their own animations, so an idle action queue is the better signal.)
static class PortGcPacer
{
	public static bool Enabled = true;

	const double CalmInputMs = 400;     // no finger and no input for this long
	const double QueueIdleMs = 400;     // in a run: the game's action queue empty and idle for this long
	const double CalmTweenMs = 200;     // outside a run: no one-shot animation for this long
	const double MinIntervalMs = 1500;  // at most one paced collection per 1.5 s
	const long MinAllocated = 4L << 20; // and only after this much allocation since the last one
	static readonly long[] Budgets = { 64L << 20, 32L << 20, 16L << 20 };

	// Summary counters (reset by PortPerf every window).
	public static int Paced, Full, Overflows;
	public static double PausedMs, MaxPauseMs;

	static int _budgetIndex;
	static bool _inRegion, _loggedBudget, _failed;
	static double _lastMs = -1e9, _queueIdleSinceMs = -1;
	static long _allocAtLast, _heapAfterFull;

	internal static void Update(double now)
	{
		if (!Enabled || _failed)
			return;
		bool inRegion = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
		if (_inRegion && !inRegion)
			Overflows++; // the budget ran out (or something else collected): the runtime did a normal collection
		_inRegion = inRegion;
		if (!Calm(now))
			return;
		if (now - _lastMs < MinIntervalMs)
			return;
		long allocated = GC.GetTotalAllocatedBytes(false);
		if (allocated - _allocAtLast < MinAllocated)
			return;
		_lastMs = now;
		_allocAtLast = allocated;
		long start = Stopwatch.GetTimestamp();
		try
		{
			if (inRegion)
			{
				try { GC.EndNoGCRegion(); }
				catch (InvalidOperationException) { } // already ended by an allocation overflow or induced GC
			}
			long heap = GC.GetGCMemoryInfo().HeapSizeBytes;
			bool full = _heapAfterFull == 0 || heap > _heapAfterFull + Math.Max(_heapAfterFull / 2, 32L << 20);
			GC.Collect(full ? 2 : 1, GCCollectionMode.Forced, blocking: true, compacting: false);
			if (full)
			{
				_heapAfterFull = GC.GetGCMemoryInfo().HeapSizeBytes;
				Full++;
			}
			_inRegion = StartRegion();
		}
		catch (Exception e)
		{
			_failed = true; // never let pacing break the game: fall back to the runtime's own scheduling
			GD.Print("[PORT] gc pacer disabled: " + e.Message);
			return;
		}
		double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
		Paced++;
		PausedMs += ms;
		MaxPauseMs = Math.Max(MaxPauseMs, ms);
	}

	static bool Calm(double now)
	{
		var run = RunManager.Instance;
		var queues = run?.ActionQueueSet;
		var executor = run?.ActionExecutor;
		bool inRun = queues != null && executor != null;
		if (inRun && queues!.IsEmpty && !executor!.IsRunning)
		{
			if (_queueIdleSinceMs < 0) _queueIdleSinceMs = now;
		}
		else
			_queueIdleSinceMs = -1;
		if (PortFrameRate.FingerDown || PortFrameRate.MsSinceInput < CalmInputMs)
			return false;
		if (inRun)
			return _queueIdleSinceMs >= 0 && now - _queueIdleSinceMs >= QueueIdleMs;
		return PortFrameRate.MsSinceTween >= CalmTweenMs;
	}

	static bool StartRegion()
	{
		while (_budgetIndex < Budgets.Length)
		{
			try
			{
				// The collection just emptied gen0, so this reserves space without collecting again.
				bool ok = GC.TryStartNoGCRegion(Budgets[_budgetIndex], disallowFullBlockingGC: true);
				if (!_loggedBudget)
				{
					_loggedBudget = true;
					GD.Print($"[PORT] gc pacer: collections at calm moments, no-GC budget {Budgets[_budgetIndex] >> 20} MB (started={ok})");
				}
				return ok;
			}
			catch (ArgumentOutOfRangeException)
			{
				_budgetIndex++; // larger than this runtime allows: try a smaller budget
			}
		}
		return false;
	}
}
