#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Environment = System.Environment;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Port;

namespace PortMultiplayer;

// Player names in ENet games. Without Steam the game has no names for other players, so port devices swap them
// over a small UDP side channel next to the game's port. A joining device sends "<id>\t<name>" to the host;
// the host answers with everyone's names, and pushes the table to every device it has heard from when someone
// new arrives. A PC host doesn't answer, so its players keep the generic names.
//
// The same channel carries a small heartbeat each way (40 per second): an iPhone's Wi-Fi dozes between packets
// when traffic is sparse, which on its own added 20-100 ms to the game's messages (measured: 1 ping/s averaged
// 20 ms, max 95; 50/s averaged 3 ms, max 6).
static class PortNames
{
	const ushort NamePort = 33772; // the game's ENet port + 1
	const string Magic = "sts2port-names 1";
	const string ConfigPath = "user://port_mp.cfg";
	const string Section = "names"; // names of past players, for run history (not the host's id 1, which varies)
	static readonly byte[] Heartbeat = Encoding.UTF8.GetBytes("sts2port-hb");
	const int HeartbeatMs = 25;
	// Wi-Fi discovery (PortDiscovery.cs): a probe gets "sts2port-host 1\n<name>\t<players>\t<max>\t<in run 0|1>\t<version>".
	public static readonly byte[] DiscoverProbe = Encoding.UTF8.GetBytes("sts2port-discover 1");
	public const string HostReplyMagic = "sts2port-host 1";
	static volatile byte[]? _hostInfo; // refreshed every second on the main thread while hosting

	static readonly ConcurrentDictionary<ulong, string> _names = new();
	static CancellationTokenSource? _session;
	static bool _loaded;

	static void Log(string msg) => GD.Print("[MP] " + msg);

	// The version the game's own join compares (JoinFlow): release version, else the build's commit id.
	public static string GameVersion => MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.ReleaseInfo?.Version
		?? MegaCrit.Sts2.Core.Debug.GitHelper.ShortCommitId ?? "UNKNOWN";

	public static string Fallback(ulong id) => id == 1 ? "Host" : $"Player {id}";

	public static string? MyName => PortHooks.PersonaName is { Length: > 0 } n && Clean(n) is { Length: > 0 } c ? c : null;

	// Main thread (loads saved names on first use).
	public static string? Lookup(ulong id)
	{
		if (!_loaded) LoadSaved();
		return Known(id);
	}

	static string? Known(ulong id) => _names.TryGetValue(id, out var name) ? name : null;

	// ---- host ----

	public static void StartHost(NetHostGameService service)
	{
		if (!_loaded) LoadSaved();
		var session = NewSession();
		_ = Task.Run(() => HostLoop(session.Token));
		_ = StopWhenHostingEnds(service, session);
	}

	static async Task StopWhenHostingEnds(NetHostGameService service, CancellationTokenSource session)
	{
		var tree = (SceneTree)Engine.GetMainLoop();
		await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame); // StartENetHost is still running
		while (!session.IsCancellationRequested && service.IsConnected)
		{
			// The lobby is part of the main menu; once a run starts, joining isn't possible.
			bool inRun = MegaCrit.Sts2.Core.Nodes.NGame.Instance?.MainMenu == null;
			string version = GameVersion;
			_hostInfo = Encoding.UTF8.GetBytes($"{HostReplyMagic}\n{MyName ?? ""}\t{service.ConnectedPeers.Count + 1}\t" +
				$"{PortHooks.LanMaxPlayers(4)}\t{(inRun ? 1 : 0)}\t{Clean(version)}");
			await tree.ToSignal(tree.CreateTimer(1.0, true, false, true), SceneTreeTimer.SignalName.Timeout);
		}
		_hostInfo = null;
		session.Cancel();
	}

	static async Task HostLoop(CancellationToken ct)
	{
		using var udp = await Bind(ct);
		if (udp == null) return;
		using var _ = ct.Register(udp.Close);
		var peers = new Dictionary<ulong, IPEndPoint>();
		while (!ct.IsCancellationRequested)
		{
			UdpReceiveResult r;
			try { r = await udp.ReceiveAsync(ct); }
			catch (SocketException) { continue; } // e.g. port unreachable from a device that left
			catch (Exception) { break; }
			if (r.Buffer.AsSpan().SequenceEqual(DiscoverProbe))
			{
				if (_hostInfo is { } info)
					try { await udp.SendAsync(info, r.RemoteEndPoint, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
				continue;
			}
			if (r.Buffer.AsSpan().SequenceEqual(Heartbeat))
			{
				try { await udp.SendAsync(Heartbeat, r.RemoteEndPoint, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
				continue;
			}
			if (Decode(r.Buffer) is not [var (id, name), ..] || id <= 1) continue;
			bool isNew = !peers.TryGetValue(id, out var known) || !known.Equals(r.RemoteEndPoint);
			peers[id] = r.RemoteEndPoint;
			bool changed = Merge(new[] { (id, name) }, 1);
			var table = new List<(ulong, string)>();
			if (MyName is { } me) table.Add((1, me));
			foreach (var peer in peers.Keys)
				if (Known(peer) is { } n) table.Add((peer, n));
			byte[] payload = Encode(table);
			foreach (var to in changed || isNew ? peers.Values.ToList() : new List<IPEndPoint> { r.RemoteEndPoint })
				try { await udp.SendAsync(payload, to, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
		}
	}

	static async Task<UdpClient?> Bind(CancellationToken ct)
	{
		for (int attempt = 0; ; attempt++)
		{
			var udp = new UdpClient(AddressFamily.InterNetwork);
			try
			{
				udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
				udp.Client.Bind(new IPEndPoint(IPAddress.Any, NamePort));
				return udp;
			}
			catch (SocketException e)
			{
				udp.Dispose();
				if (attempt >= 5 || ct.IsCancellationRequested)
				{
					Log($"player names unavailable (port {NamePort}: {e.Message})");
					return null;
				}
				try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return null; }
			}
		}
	}

	// ---- join ----

	// Introduces this device to the host and keeps the names current for the session. Waits briefly for the first
	// answer so the lobby shows names straight away.
	public static async Task StartClient(string hostIp, ulong self)
	{
		if (!_loaded) LoadSaved();
		var session = NewSession();
		var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = Task.Run(() => ClientLoop(hostIp, self, first, session.Token));
		await Task.WhenAny(first.Task, Task.Delay(1500));
		if (!first.Task.IsCompleted) Log("no player names from the host yet (a PC host doesn't send them)");
	}

	static async Task ClientLoop(string hostIp, ulong self, TaskCompletionSource first, CancellationToken session)
	{
		using var stop = CancellationTokenSource.CreateLinkedTokenSource(session);
		var ct = stop.Token;
		using var udp = new UdpClient(AddressFamily.InterNetwork);
		try { udp.Connect(IPAddress.Parse(hostIp), NamePort); }
		catch (Exception e) { Log("player names: " + e.Message); return; }
		using var _ = ct.Register(udp.Close);
		long lastHeard = Environment.TickCount64;
		var receive = Task.Run(async () =>
		{
			while (!ct.IsCancellationRequested)
			{
				try
				{
					var r = await udp.ReceiveAsync(ct);
					Interlocked.Exchange(ref lastHeard, Environment.TickCount64);
					if (Decode(r.Buffer) is not { } table) continue;
					Merge(table, self);
					first.TrySetResult();
				}
				catch (SocketException) { try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { } }
				catch (Exception) { break; }
			}
		});
		byte[] hello = Encode(new[] { (self, MyName ?? "") });
		// Heartbeat continuously and repeat the hello every 3 s (the host answers each); stop once the host has been
		// silent for 30 s or the game session has ended.
		using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HeartbeatMs));
		long started = Environment.TickCount64;
		for (int tick = 0; !ct.IsCancellationRequested; tick++)
		{
			long now = Environment.TickCount64;
			if (now - Interlocked.Read(ref lastHeard) > 30_000) break;
			if (now - started > 20_000 && !PortKeepAlive.SessionActive) break;
			try { await udp.SendAsync(tick % (3000 / HeartbeatMs) == 0 ? hello : Heartbeat, ct); }
			catch (SocketException) { }
			catch (Exception) { break; }
			try { await timer.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { break; }
		}
		stop.Cancel();
		await receive;
	}

	// ---- shared ----

	static CancellationTokenSource NewSession()
	{
		_session?.Cancel();
		_names.TryRemove(1, out _); // id 1 is whoever hosts this time
		return _session = new CancellationTokenSource();
	}

	public static string Clean(string s)
	{
		s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
		return s.Length > 32 ? s[..32] : s;
	}

	static byte[] Encode(IEnumerable<(ulong Id, string Name)> entries) =>
		Encoding.UTF8.GetBytes(Magic + "\n" + string.Join("\n", entries.Select(e => $"{e.Id}\t{Clean(e.Name)}")));

	static List<(ulong Id, string Name)>? Decode(byte[] data)
	{
		var lines = Encoding.UTF8.GetString(data).Split('\n');
		if (lines[0] != Magic) return null;
		var entries = new List<(ulong, string)>();
		foreach (var line in lines.Skip(1))
		{
			int tab = line.IndexOf('\t');
			if (tab > 0 && ulong.TryParse(line.AsSpan(0, tab), out ulong id)) entries.Add((id, Clean(line[(tab + 1)..])));
		}
		return entries;
	}

	// Records new names (never this device's own) and updates what's on screen. True if anything changed.
	static bool Merge(IEnumerable<(ulong Id, string Name)> entries, ulong self)
	{
		var changed = entries.Where(e => e.Id != self && e.Name.Length > 0 && Known(e.Id) != e.Name).ToList();
		if (changed.Count == 0) return false;
		foreach (var (id, name) in changed) _names[id] = name;
		Log("player names: " + string.Join(", ", changed.Select(e => $"{e.Id} = {e.Name}")));
		Callable.From(() =>
		{
			Save(changed);
			RefreshNameplates(changed.Select(e => e.Id).ToHashSet());
		}).CallDeferred();
		return true;
	}

	// The lobby list and the in-run player panels set their nameplate once; update any still showing a placeholder.
	static void RefreshNameplates(HashSet<ulong> ids)
	{
		foreach (var node in ((SceneTree)Engine.GetMainLoop()).Root.FindChildren("*", "", true, false))
		{
			ulong id;
			if (node is NRemoteLobbyPlayer lobby) id = lobby.PlayerId;
			else if (node is NMultiplayerPlayerState { Player: { } player }) id = player.NetId;
			else continue;
			if (!ids.Contains(id) || Lookup(id) is not { } name) continue;
			if (node.Get("_nameplateLabel").As<MegaLabel>() is { } label && label.Text == Fallback(id))
				label.SetTextAutoSize(name);
		}
	}

	static void LoadSaved()
	{
		_loaded = true;
		var cfg = new ConfigFile();
		if (cfg.Load(ConfigPath) != Error.Ok || !cfg.HasSection(Section)) return;
		foreach (string key in cfg.GetSectionKeys(Section))
			if (ulong.TryParse(key, out ulong id) && id > 1) _names.TryAdd(id, cfg.GetValue(Section, key).AsString());
	}

	static void Save(List<(ulong Id, string Name)> changed)
	{
		if (changed.All(e => e.Id <= 1)) return;
		var cfg = new ConfigFile();
		cfg.Load(ConfigPath);
		foreach (var (id, name) in changed)
			if (id > 1) cfg.SetValue(Section, id.ToString(), name);
		cfg.Save(ConfigPath);
	}
}
