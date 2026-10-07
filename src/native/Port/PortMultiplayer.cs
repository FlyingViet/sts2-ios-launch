#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Port;

namespace PortMultiplayer;

// iOS port: multiplayer over the game's ENet (direct IP) transport, which it uses whenever Steamworks isn't
// running. sts2.dll is IL-patched (tools/patcher) so the Join screen asks for the host's IP (the game joins
// 127.0.0.1), hosting reports this device's addresses, and a joining device uses its own player id.
static class PortMp
{
	const ushort Port = 33771; // NMultiplayerSubmenu.StartENetHost / ENetClientConnectionInitializer
	const string ConfigPath = "user://port_mp.cfg";
	static readonly Regex Ipv4 = new(@"^\d{1,3}(\.\d{1,3}){3}$");

	static Node? _ui;
	static TaskCompletionSource<(string Kind, string Arg)>? _choice;
	static int _hostGeneration;

	[ModuleInitializer]
	internal static void Init()
	{
		PortHooks.MpJoin = JoinAsync;
		PortHooks.OnENetHost = OnHost;
		PortHooks.NullPlayerName = PlayerName;
	}

	static void Log(string msg) => GD.Print("[MP] " + msg);

	static ulong LocalId => PortHooks.NullPlayerId != 0 ? PortHooks.NullPlayerId : 1;

	static string PlayerName(ulong id)
	{
		if (id == LocalId && PortHooks.PersonaName is { Length: > 0 } me) return me;
		return id == 1 ? "Host" : $"Player {id}";
	}

	static ConfigFile Config()
	{
		var cfg = new ConfigFile();
		cfg.Load(ConfigPath);
		return cfg;
	}

	// Stable per-device id when joining (the host is always 1; the game's default client id 1000 would collide
	// when two port devices join the same host).
	static ulong ClientId()
	{
		var cfg = Config();
		long id = cfg.GetValue("mp", "client_id", 0L).AsInt64();
		if (id <= 1000)
		{
			id = Random.Shared.Next(1001, 1_000_000);
			cfg.SetValue("mp", "client_id", id);
			cfg.Save(ConfigPath);
		}
		return (ulong)id;
	}

	// ---- join ----

	static async Task JoinAsync(NJoinFriendScreen screen)
	{
		var cfg = Config();
		string ip = cfg.GetValue("mp", "last_ip", "").AsString();
		var (wifi, tailscale) = Addresses();
		PokeLocalNetwork(wifi); // brings up iOS's Local Network prompt while the IP is being typed
		string error = "";
		while (true)
		{
			EnsureUi().Call("show_join", ip, error);
			var (kind, arg) = await NextChoice();
			if (kind != "join")
			{
				CloseUi();
				// Leave the (empty) join screen as if its back button was pressed.
				var back = screen.GetNodeOrNull<Control>("BackButton");
				back?.EmitSignal("Released", back);
				return;
			}
			ip = arg.Trim();
			if (Ipv4.IsMatch(ip) && IPAddress.TryParse(ip, out _)) break;
			error = "That isn't an IP address. It looks like 192.168.1.20.";
		}
		cfg.SetValue("mp", "last_ip", ip);
		cfg.Save(ConfigPath);
		CloseUi();
		ulong id = ClientId();
		PortHooks.NullPlayerId = id;
		Log($"joining {ip}:{Port} as player {id} (this device: {wifi ?? "no Wi-Fi"}{(tailscale != null ? ", Tailscale " + tailscale : "")})");
		await screen.JoinGameAsync(new ENetClientConnectionInitializer(id, ip, Port));
	}

	// ---- host ----

	static void OnHost(NetHostGameService service)
	{
		PortHooks.NullPlayerId = 0; // the ENet host is player 1, which is also the null platform's own id
		int generation = ++_hostGeneration;
		_ = HostBannerLoop(service, generation);
	}

	static async Task HostBannerLoop(NetHostGameService service, int generation)
	{
		var tree = (SceneTree)Engine.GetMainLoop();
		await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame); // StartENetHost is still running
		var (wifi, tailscale) = Addresses();
		PokeLocalNetwork(wifi);
		var where = new List<string>();
		if (wifi != null) where.Add(wifi);
		if (tailscale != null) where.Add($"{tailscale} (Tailscale)");
		string address = where.Count > 0 ? string.Join(" or ", where) : "this device's IP address";
		Log($"hosting on {address}, port {Port}");
		// The lobby (character select) is part of the main menu; hide once the run starts or hosting stops.
		while (generation == _hostGeneration && service.IsConnected && NGame.Instance?.MainMenu != null)
		{
			int players = service.ConnectedPeers.Count + 1;
			EnsureUi().Call("show_banner", $"Hosting · others join at {address} · {players} player{(players == 1 ? "" : "s")} here");
			await tree.ToSignal(tree.CreateTimer(1.0, true, false, true), SceneTreeTimer.SignalName.Timeout);
		}
		if (generation == _hostGeneration && _ui != null && GodotObject.IsInstanceValid(_ui)) _ui.Call("hide_banner");
	}

	// ---- network helpers ----

	// Wi-Fi (en0) and Tailscale (utun, 100.64.0.0/10) IPv4 addresses.
	static (string? Wifi, string? Tailscale) Addresses()
	{
		string? wifi = null, tailscale = null;
		foreach (var iface in IP.GetLocalInterfaces())
		{
			string name = iface["name"].AsString();
			foreach (var v in iface["addresses"].AsGodotArray())
			{
				string a = v.AsString();
				if (!IPAddress.TryParse(a, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) continue;
				var b = addr.GetAddressBytes();
				if (name == "en0" && b[0] != 169) wifi ??= a;
				else if (name.StartsWith("utun") && b[0] == 100 && b[1] >= 64 && b[1] < 128) tailscale ??= a;
			}
		}
		return (wifi, tailscale);
	}

	// iOS asks for Local Network access on the first LAN packet; send a harmless one (UDP discard port on
	// the router) so the prompt appears up front instead of dropping the first connection attempt.
	static void PokeLocalNetwork(string? wifi)
	{
		if (wifi == null) return;
		try
		{
			var parts = wifi.Split('.');
			var udp = new PacketPeerUdp();
			udp.SetDestAddress($"{parts[0]}.{parts[1]}.{parts[2]}.1", 9);
			udp.PutPacket(new byte[] { 0 });
			udp.Close();
		}
		catch (Exception e) { Log("local network probe failed: " + e.Message); }
	}

	// ---- UI (res://port/mp_ui.gd) ----

	static Node EnsureUi()
	{
		if (_ui != null && GodotObject.IsInstanceValid(_ui)) return _ui;
		var script = GD.Load<GDScript>("res://port/mp_ui.gd");
		_ui = (Node)script.New().AsGodotObject();
		_ui.Connect("join_submitted", Callable.From<string>(ip => _choice?.TrySetResult(("join", ip))));
		_ui.Connect("cancelled", Callable.From(() => _choice?.TrySetResult(("cancel", ""))));
		((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, _ui);
		return _ui;
	}

	static Task<(string Kind, string Arg)> NextChoice()
	{
		_choice = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
		return _choice.Task;
	}

	static void CloseUi()
	{
		if (_ui != null && GodotObject.IsInstanceValid(_ui)) _ui.Call("close_join");
	}
}
