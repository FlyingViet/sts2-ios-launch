#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Port;

namespace PortMultiplayer;

// iOS port: multiplayer over the game's ENet (direct IP) transport, which it uses whenever Steamworks isn't
// running. sts2.dll is IL-patched (tools/patcher) so the Join screen asks for the host's IP (the game joins
// 127.0.0.1), hosting reports this device's addresses, and a joining device uses its own player id.
// An iOS host takes up to 6 players (the game's limit is 4): see the "six players" section below.
static class PortMp
{
	const ushort Port = 33771; // NMultiplayerSubmenu.StartENetHost / ENetClientConnectionInitializer
	const string ConfigPath = "user://port_mp.cfg";
	const int MaxPlayers = 6;
	// Testing: with this file present, the next hosted lobby also lists that many fake players (UI preview only;
	// the file is deleted once used).
	const string PreviewFlag = "user://port_mp_preview";
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
		PortHooks.RestSiteSeats = AddRestSiteSeats;
		PortHooks.TreasureRelicHolders = AddTreasureRelicHolders;
		PortHooks.AfterEssentialInit += EnableSixPlayers;
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
		bool previewed = false;
		while (generation == _hostGeneration && service.IsConnected && NGame.Instance?.MainMenu != null)
		{
			int players = service.ConnectedPeers.Count + 1;
			int cap = PortHooks.LanMaxPlayers(4);
			EnsureUi().Call("show_banner", $"Hosting · others join at {address} · {players} of {cap} players here");
			if (!previewed) previewed = PreviewLobby(tree);
			await tree.ToSignal(tree.CreateTimer(1.0, true, false, true), SceneTreeTimer.SignalName.Timeout);
		}
		if (generation == _hostGeneration && _ui != null && GodotObject.IsInstanceValid(_ui)) _ui.Call("hide_banner");
	}

	// ---- six players ----
	// The game hosts at most 4. Raising the cap (PortHooks.LanPlayerCap, read where the game passed 4) needs:
	//  - LobbyPlayer.slotId: 2 bits on the wire; slots 4-5 carry the extra bit (patched Serialize/Deserialize),
	//  - rest site seats and treasure relic holders for players 5-6 (the scenes have four of each).
	// A PC can't join an iOS host (its Join is localhost-only), so only patched devices see slots 4-5.

	// Checks the patched LobbyPlayer encoding before allowing more than 4 players: slots 0-5 must round-trip and
	// slots 0-3 must produce exactly the game's original bytes.
	static void EnableSixPlayers()
	{
		try
		{
			var character = ModelDb.Character<Ironclad>();
			var writer = new PacketWriter { WarnOnGrow = false };
			var vanilla = new PacketWriter { WarnOnGrow = false };
			var reader = new PacketReader();
			for (int slot = 0; slot < MaxPlayers; slot++)
			{
				var p = new LobbyPlayer
				{
					id = 1001UL + (ulong)slot, slotId = slot, character = character, unlockState = new SerializableUnlockState(),
					maxMultiplayerAscensionUnlocked = 3 * slot, isReady = slot % 2 == 1,
				};
				writer.Reset();
				p.Serialize(writer);
				byte[] bytes = writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
				reader.Reset(bytes);
				var q = new LobbyPlayer();
				q.Deserialize(reader);
				if (q.id != p.id || q.slotId != p.slotId || q.maxMultiplayerAscensionUnlocked != p.maxMultiplayerAscensionUnlocked
					|| q.isReady != p.isReady || q.character != p.character)
					throw new Exception($"slot {slot} read back as slot {q.slotId}, ascension {q.maxMultiplayerAscensionUnlocked}");
				if (slot < 4)
				{
					vanilla.Reset();
					vanilla.WriteULong(p.id);
					vanilla.WriteInt(p.slotId, 2);
					vanilla.WriteModel(p.character);
					vanilla.Write(p.unlockState);
					vanilla.WriteInt(p.maxMultiplayerAscensionUnlocked);
					vanilla.WriteBool(p.isReady);
					if (!vanilla.Buffer.AsSpan(0, vanilla.BytePosition).SequenceEqual(bytes))
						throw new Exception($"slot {slot} no longer matches the game's own encoding");
				}
			}
			PortHooks.LanPlayerCap = MaxPlayers;
			Log($"hosting allows up to {MaxPlayers} players (lobby encoding checked)");
		}
		catch (Exception e)
		{
			PortHooks.LanPlayerCap = 0;
			Log("hosting stays at 4 players: lobby encoding check failed: " + e.Message);
		}
	}

	// Rest site: two seats on each log around the fire; players 5 and 6 sit on the grass just beyond the logs
	// (BgContainer coordinates, like the scene's Character_1..4 at (625,642) (1239,649) (750,585) (1092,583)).
	static readonly Vector2[] ExtraSeats = { new(445, 712), new(1420, 716) };

	static void AddRestSiteSeats(NRestSiteRoom room, List<Control> seats)
	{
		var template = seats[^1];
		var parent = template.GetParent();
		for (int i = seats.Count; i < MaxPlayers && i - 4 < ExtraSeats.Length; i++)
		{
			var seat = (Control)template.Duplicate();
			seat.Name = $"Character_{i + 1}";
			var at = ExtraSeats[i - 4];
			seat.OffsetLeft = seat.OffsetRight = at.X;
			seat.OffsetTop = seat.OffsetBottom = at.Y;
			parent.AddChild(seat); // last = drawn in front, as they're nearer than the log seats
			seats.Add(seat);
		}
	}

	// Treasure: one relic per player on holders around the chest. The scene's four (136 px) fill a diamond:
	// top-left and bottom-right of the centre, far left, far right. Players 5 and 6 get the two free centre corners.
	static readonly Vector2[] ExtraHolders = { new(-148, 44), new(12, -180) };
	const string HolderScene = "res://scenes/ui/treasure_relic_holder.tscn";

	static void AddTreasureRelicHolders(Control container)
	{
		if (container.GetNodeOrNull("MultiplayerRelicHolder4") == null || container.HasNode("MultiplayerRelicHolder5")) return;
		var scene = GD.Load<PackedScene>(HolderScene);
		for (int i = 0; i < ExtraHolders.Length; i++)
		{
			var holder = scene.Instantiate<Control>();
			holder.Name = $"MultiplayerRelicHolder{5 + i}";
			holder.Visible = false;
			holder.SetAnchorsPreset(Control.LayoutPreset.Center);
			holder.GrowHorizontal = holder.GrowVertical = Control.GrowDirection.Both;
			var at = ExtraHolders[i];
			holder.OffsetLeft = at.X;
			holder.OffsetTop = at.Y;
			holder.OffsetRight = at.X + 136;
			holder.OffsetBottom = at.Y + 136;
			container.AddChild(holder); // after holders 1-4: the game numbers holders in child order
		}
	}

	// UI preview: adds fake players to a hosted lobby's player list so a full lobby can be checked on one device.
	static bool PreviewLobby(SceneTree tree)
	{
		if (!FileAccess.FileExists(PreviewFlag)) return true;
		var screen = tree.Root.FindChildren("*", "", true, false)
			.OfType<NCharacterSelectScreen>().FirstOrDefault(s => s.IsVisibleInTree());
		if (screen == null) return false;
		var list = screen.Get("_remotePlayerContainer").As<NRemoteLobbyPlayerContainer>();
		if (list == null) return false;
		int fakes = Math.Min(FileAccess.GetFileAsString(PreviewFlag).Trim().ToInt(), MaxPlayers - 1);
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(PreviewFlag)); // one-shot
		for (int i = 0; i < fakes; i++)
			list.OnPlayerConnected(new LobbyPlayer
			{
				id = 900001UL + (ulong)i, slotId = i + 1, character = ModelDb.Character<Ironclad>(),
				unlockState = new SerializableUnlockState(),
			});
		Log($"lobby preview: added {Math.Max(fakes, 0)} fake player(s)");
		return true;
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
