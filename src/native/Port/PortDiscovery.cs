#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Environment = System.Environment;

namespace PortMultiplayer;

// Finds games hosted by iPhones/iPads on the same Wi-Fi (and at recently joined addresses, e.g. over Tailscale)
// while the Join dialog is open. iOS only lets apps broadcast or multicast with a restricted entitlement, so this
// sends a small unicast probe to every address on the Wi-Fi subnet; hosts answer from their name service (UDP
// 33772, PortNames.cs). PC hosts don't run that service: join those by IP.
static class PortDiscovery
{
	public sealed record Host(string Ip, string Name, int Players, int Max, bool InRun, string Version);

	const ushort NamePort = 33772;
	const int SweepMs = 1500, ForgetMs = 5000, MaxSubnetHosts = 1022;

	static CancellationTokenSource? _scan;

	static void Log(string msg) => GD.Print("[MP] " + msg);

	// Main thread. Calls onChange (from a worker thread) with the current list whenever it changes, until Stop().
	public static void Start(IEnumerable<string> recent, Action<List<Host>> onChange)
	{
		Stop();
		var scan = _scan = new CancellationTokenSource();
		var (self, targets) = Targets();
		foreach (var ip in recent)
			if (IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork && !targets.Contains(a))
				targets.Add(a);
		Log($"looking for games: {targets.Count} address(es){(self != null ? $" around {self}" : " (no Wi-Fi)")}");
		_ = Task.Run(() => Scan(self, targets, onChange, scan.Token));
	}

	public static void Stop()
	{
		_scan?.Cancel();
		_scan = null;
	}

	static async Task Scan(string? self, List<IPAddress> targets, Action<List<Host>> onChange, CancellationToken ct)
	{
		using var udp = new UdpClient(AddressFamily.InterNetwork);
		using var _ = ct.Register(udp.Close);
		var seen = new ConcurrentDictionary<string, (Host Host, long At)>();
		string lastSent = "";
		void Publish()
		{
			long now = Environment.TickCount64;
			var hosts = seen.Values.Where(v => now - v.At < ForgetMs).Select(v => v.Host)
				.OrderBy(h => h.InRun).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
			string key = string.Join("|", hosts);
			if (key == lastSent) return;
			lastSent = key;
			onChange(hosts);
		}
		var receive = Task.Run(async () =>
		{
			while (!ct.IsCancellationRequested)
			{
				try
				{
					var r = await udp.ReceiveAsync(ct);
					string ip = r.RemoteEndPoint.Address.ToString();
					if (ip == self || Parse(ip, r.Buffer) is not { } host) continue;
					seen[ip] = (host, Environment.TickCount64);
					lock (seen) Publish();
				}
				catch (SocketException) { }
				catch (Exception) { break; }
			}
		});
		while (!ct.IsCancellationRequested)
		{
			foreach (var target in targets)
			{
				if (ct.IsCancellationRequested) break;
				try { await udp.SendAsync(PortNames.DiscoverProbe, new IPEndPoint(target, NamePort), ct); }
				catch (SocketException) { } // unreachable addresses, Local Network permission not granted yet
				catch (Exception) { break; }
			}
			try { await Task.Delay(SweepMs, ct); } catch (OperationCanceledException) { break; }
			lock (seen) Publish(); // drops hosts that stopped answering
		}
		await receive;
	}

	static Host? Parse(string ip, byte[] data)
	{
		var lines = Encoding.UTF8.GetString(data).Split('\n');
		if (lines.Length < 2 || lines[0] != PortNames.HostReplyMagic) return null;
		var f = lines[1].Split('\t');
		if (f.Length < 5 || !int.TryParse(f[1], out int players) || !int.TryParse(f[2], out int max)) return null;
		return new Host(ip, PortNames.Clean(f[0]), players, max, f[3] == "1", PortNames.Clean(f[4]));
	}

	// This device's Wi-Fi address and every other address on its subnet (at most /22; larger networks: the /24).
	static (string? Self, List<IPAddress> Targets) Targets()
	{
		var targets = new List<IPAddress>();
		try
		{
			foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (nic.Name != "en0" || nic.OperationalStatus == OperationalStatus.Down) continue;
				foreach (var u in nic.GetIPProperties().UnicastAddresses)
				{
					if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
					byte[] a = u.Address.GetAddressBytes();
					if (a[0] == 169 && a[1] == 254) continue;
					uint ip = (uint)(a[0] << 24 | a[1] << 16 | a[2] << 8 | a[3]);
					int prefix = u.PrefixLength is > 0 and <= 32 ? u.PrefixLength : 24;
					if ((1u << (32 - prefix)) - 2 > MaxSubnetHosts) prefix = 24;
					uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
					uint net = ip & mask, broadcast = net | ~mask;
					for (uint h = net + 1; h < broadcast; h++)
						if (h != ip)
							targets.Add(new IPAddress(new[] { (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h }));
					return (u.Address.ToString(), targets);
				}
			}
		}
		catch (Exception e) { Log("Wi-Fi address lookup failed: " + e.Message); }
		// Fallback: Godot's interface list, assuming a /24.
		foreach (var iface in IP.GetLocalInterfaces())
		{
			if (iface["name"].AsString() != "en0") continue;
			foreach (var v in iface["addresses"].AsGodotArray())
			{
				if (!IPAddress.TryParse(v.AsString(), out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) continue;
				byte[] a = addr.GetAddressBytes();
				if (a[0] == 169 && a[1] == 254) continue;
				for (int h = 1; h < 255; h++)
					if (h != a[3]) targets.Add(new IPAddress(new[] { a[0], a[1], a[2], (byte)h }));
				return (addr.ToString(), targets);
			}
		}
		return (null, targets);
	}
}
