#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortSteam;

public sealed class SteamException : Exception
{
	public readonly int Result;
	public SteamException(int result, string message) : base($"{message}: {Name(result)} ({result})") => Result = result;

	// Results that mean the saved refresh token is no longer usable.
	public bool IsAuthFailure => Result is 5 or 15 or 26 or 27 or 8 or 63;

	public static string Name(int r) => r switch
	{
		1 => "OK", 2 => "Fail", 3 => "NoConnection", 5 => "InvalidPassword", 6 => "LoggedInElsewhere",
		8 => "InvalidParam", 9 => "FileNotFound", 10 => "Busy", 11 => "InvalidState", 15 => "AccessDenied",
		16 => "Timeout", 20 => "ServiceUnavailable", 22 => "Pending", 25 => "LimitExceeded", 26 => "Revoked",
		27 => "Expired", 29 => "DuplicateRequest", 48 => "TryAnotherCM", 52 => "Cancelled", 63 => "AccountLogonDenied",
		65 => "InvalidLoginAuthCode", 84 => "RateLimitExceeded", 85 => "AccountLoginDeniedNeedTwoFactor",
		88 => "TwoFactorCodeMismatch", 108 => "TooManyPending", _ => "EResult",
	};
}

// Minimal Steam CM (connection manager) client over WebSocket: ClientHello, ClientLogon with a refresh
// token, heartbeats and unified service calls ("Service.Method#1"). Mirrors SteamKit2's wire behaviour.
public sealed class SteamCm : IDisposable
{
	public static Action<string> Log = Console.WriteLine;
	public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

	const uint ProtoMask = 0x80000000;
	const uint EMsgMulti = 1, EMsgServiceMethodResponse = 147, EMsgServiceMethodCallFromClient = 151,
		EMsgClientHeartBeat = 703, EMsgClientLogOff = 706, EMsgClientLogOnResponse = 751, EMsgClientLoggedOff = 757,
		EMsgClientAccountInfo = 768, EMsgClientLogon = 5514, EMsgServiceMethodCallFromClientNonAuthed = 9804,
		EMsgClientHello = 9805;
	public const uint ProtocolVersion = 65581;
	public const int OsType = 16; // Windows 10: the client OS type SteamKit2 users most commonly report

	static string[]? _servers;
	static int _serverCursor;

	readonly SemaphoreSlim _sendLock = new(1, 1);
	readonly ConcurrentDictionary<ulong, TaskCompletionSource<(ProtoMsg hdr, byte[] body)>> _jobs = new();
	ClientWebSocket? _ws;
	CancellationTokenSource? _recvCts;
	TaskCompletionSource<ProtoMsg>? _logonTcs;
	Timer? _heartbeat;
	long _nextJob;
	ulong _steamId;
	int _sessionId;
	volatile bool _loggedOn;

	public bool IsConnected => _ws?.State == WebSocketState.Open;
	public bool IsLoggedOn => IsConnected && _loggedOn;
	public ulong SteamId => _steamId;
	public string? PersonaName { get; private set; }

	static async Task<string[]> GetServersAsync(CancellationToken ct)
	{
		if (_servers is { Length: > 0 }) return _servers;
		var json = await Http.GetStringAsync(
			"https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=0&cmtype=websockets&maxcount=20", ct)
			.ConfigureAwait(false);
		using var doc = JsonDocument.Parse(json);
		var list = new List<string>();
		foreach (var s in doc.RootElement.GetProperty("response").GetProperty("serverlist").EnumerateArray())
			if (s.TryGetProperty("type", out var t) && t.GetString() == "websockets" && s.GetProperty("endpoint").GetString() is { } ep)
				list.Add(ep);
		if (list.Count == 0) throw new IOException("Steam directory returned no WebSocket CM servers");
		return _servers = list.ToArray();
	}

	public async Task ConnectAsync(CancellationToken ct = default)
	{
		Disconnect();
		var servers = await GetServersAsync(ct).ConfigureAwait(false);
		Exception? last = null;
		for (int attempt = 0; attempt < 3; attempt++)
		{
			string ep = servers[(int)((uint)Interlocked.Increment(ref _serverCursor) % (uint)servers.Length)];
			var ws = new ClientWebSocket();
			try
			{
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				cts.CancelAfter(TimeSpan.FromSeconds(10));
				await ws.ConnectAsync(new Uri($"wss://{ep}/cmsocket/"), cts.Token).ConfigureAwait(false);
				_ws = ws;
				_loggedOn = false;
				_steamId = 0;
				_sessionId = 0;
				_recvCts = new CancellationTokenSource();
				_ = Task.Run(() => ReceiveLoop(ws, _recvCts.Token));
				await SendAsync(EMsgClientHello, new ProtoWriter(), new ProtoWriter().U32(1, ProtocolVersion), ct).ConfigureAwait(false);
				Log($"[STEAM] connected to {ep}");
				return;
			}
			catch (Exception e) when (!ct.IsCancellationRequested)
			{
				last = e;
				ws.Dispose();
				Log($"[STEAM] connect to {ep} failed: {e.Message}");
			}
		}
		throw new IOException("Could not connect to Steam", last);
	}

	public async Task LogOnAsync(string accountName, string refreshToken, byte[] machineId, string machineName, CancellationToken ct = default)
	{
		var tcs = new TaskCompletionSource<ProtoMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
		_logonTcs = tcs;
		var hdr = new ProtoWriter().Fixed64(1, 76561197960265728UL).I32(2, 0); // individual account, desktop instance
		var body = new ProtoWriter()
			.U32(1, ProtocolVersion)
			.U32(2, 0xBAADF00D) // obfuscated private ip (0.0.0.0 ^ mask), as SteamKit2 sends
			.U32(3, 0)
			.U32(5, 1771)
			.Str(6, "english")
			.U32(7, unchecked((uint)OsType))
			.Bool(8, true)
			.Msg(11, new ProtoWriter().Fixed32(1, 0xBAADF00D))
			.Bytes(30, machineId)
			.Str(50, accountName)
			.Str(96, machineName)
			.Bool(102, true)
			.Str(108, refreshToken);
		await SendAsync(EMsgClientLogon, hdr, body, ct, raw: true).ConfigureAwait(false);
		var resp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
		int result = resp.I32(1, 2);
		if (result != 1) throw new SteamException(result, "Steam logon failed");
	}

	public async Task LogOnAnonymousAsync(CancellationToken ct = default)
	{
		var tcs = new TaskCompletionSource<ProtoMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
		_logonTcs = tcs;
		var hdr = new ProtoWriter().Fixed64(1, (1UL << 56) | (10UL << 52)).I32(2, 0); // AnonUser
		var body = new ProtoWriter().U32(1, ProtocolVersion).U32(7, unchecked((uint)OsType)).Str(6, "english").U32(3, 0)
			.Bytes(30, BuildMachineId("anon"));
		await SendAsync(EMsgClientLogon, hdr, body, ct, raw: true).ConfigureAwait(false);
		var resp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
		int result = resp.I32(1, 2);
		if (result != 1) throw new SteamException(result, "Anonymous logon failed");
	}

	// Unified service call, e.g. "Cloud.EnumerateUserFiles#1". Throws SteamException on a non-OK result.
	public async Task<ProtoMsg> CallAsync(string method, ProtoWriter body, CancellationToken ct = default, int timeoutMs = 20000)
	{
		if (!IsConnected) throw new SteamException(3, method);
		ulong job = (ulong)Interlocked.Increment(ref _nextJob);
		var tcs = new TaskCompletionSource<(ProtoMsg, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
		_jobs[job] = tcs;
		try
		{
			var hdr = new ProtoWriter().Fixed64(10, job).Str(12, method);
			await SendAsync(_loggedOn ? EMsgServiceMethodCallFromClient : EMsgServiceMethodCallFromClientNonAuthed, hdr, body, ct)
				.ConfigureAwait(false);
			var (rh, rb) = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
			int result = rh.I32(13, 2);
			if (result != 1) throw new SteamException(result, method + (rh.S(14) is { Length: > 0 } m ? $" ({m})" : ""));
			return ProtoMsg.Parse(rb);
		}
		finally
		{
			_jobs.TryRemove(job, out _);
		}
	}

	async Task SendAsync(uint emsg, ProtoWriter hdr, ProtoWriter body, CancellationToken ct, bool raw = false)
	{
		var ws = _ws ?? throw new SteamException(3, "send");
		if (!raw)
		{
			var full = new ProtoWriter();
			if (_steamId != 0) full.Fixed64(1, _steamId);
			if (_sessionId != 0) full.I32(2, _sessionId);
			hdr = full.Raw(hdr.ToArray());
		}
		await SendRaw(ws, emsg, hdr.ToArray(), body.ToArray(), ct).ConfigureAwait(false);
	}

	async Task SendRaw(ClientWebSocket ws, uint emsg, byte[] hdr, byte[] body, CancellationToken ct)
	{
		var packet = new byte[8 + hdr.Length + body.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(packet, emsg | ProtoMask);
		BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), hdr.Length);
		hdr.CopyTo(packet, 8);
		body.CopyTo(packet, 8 + hdr.Length);
		await _sendLock.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			await ws.SendAsync(packet, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
		}
		catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
		{
			throw new SteamException(3, "send failed: " + e.Message);
		}
		finally
		{
			_sendLock.Release();
		}
	}

	async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
	{
		var buf = new byte[64 * 1024];
		using var ms = new MemoryStream();
		try
		{
			while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
			{
				ms.SetLength(0);
				WebSocketReceiveResult r;
				do
				{
					r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
					if (r.MessageType == WebSocketMessageType.Close) return;
					ms.Write(buf, 0, r.Count);
				} while (!r.EndOfMessage);
				try { HandlePacket(ms.ToArray()); }
				catch (Exception e) { Log("[STEAM] bad packet: " + e.Message); }
			}
		}
		catch (Exception e) when (!ct.IsCancellationRequested)
		{
			Log("[STEAM] connection lost: " + e.Message);
		}
		catch { }
		finally
		{
			if (ReferenceEquals(_ws, ws)) OnDisconnected();
		}
	}

	void HandlePacket(byte[] data)
	{
		uint raw = BinaryPrimitives.ReadUInt32LittleEndian(data);
		uint emsg = raw & ~ProtoMask;
		if ((raw & ProtoMask) == 0) return; // legacy non-protobuf messages aren't needed here
		int hlen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
		var hdr = ProtoMsg.Parse(data.AsSpan(8, hlen));
		var body = data.AsSpan(8 + hlen).ToArray();
		switch (emsg)
		{
			case EMsgMulti:
				var multi = ProtoMsg.Parse(body);
				var payload = multi.Bytes(2) ?? Array.Empty<byte>();
				if (multi.U(1) > 0)
				{
					using var gz = new GZipStream(new MemoryStream(payload), CompressionMode.Decompress);
					using var outMs = new MemoryStream();
					gz.CopyTo(outMs);
					payload = outMs.ToArray();
				}
				for (int off = 0; off + 4 <= payload.Length;)
				{
					int n = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(off));
					HandlePacket(payload.AsSpan(off + 4, n).ToArray());
					off += 4 + n;
				}
				break;
			case EMsgClientLogOnResponse:
				var resp = ProtoMsg.Parse(body);
				if (resp.I32(1, 2) == 1)
				{
					_steamId = hdr.U(1);
					_sessionId = hdr.I32(2);
					_loggedOn = true;
					int hb = Math.Max(5, resp.I32(2, 9));
					_heartbeat?.Dispose();
					_heartbeat = new Timer(_ => _ = Heartbeat(), null, hb * 1000, hb * 1000);
				}
				_logonTcs?.TrySetResult(resp);
				break;
			case EMsgClientLoggedOff:
				_loggedOn = false;
				Log($"[STEAM] logged off by server: {SteamException.Name(ProtoMsg.Parse(body).I32(1, 2))}");
				Disconnect();
				break;
			case EMsgClientAccountInfo:
				PersonaName = ProtoMsg.Parse(body).S(1);
				break;
			case EMsgServiceMethodResponse:
				if (_jobs.TryGetValue(hdr.U(11, ulong.MaxValue), out var tcs)) tcs.TrySetResult((hdr, body));
				break;
		}
	}

	async Task Heartbeat()
	{
		try { if (IsLoggedOn) await SendAsync(EMsgClientHeartBeat, new ProtoWriter(), new ProtoWriter(), default).ConfigureAwait(false); }
		catch { }
	}

	void OnDisconnected()
	{
		_loggedOn = false;
		_heartbeat?.Dispose();
		_heartbeat = null;
		_logonTcs?.TrySetException(new SteamException(3, "disconnected during logon"));
		foreach (var kv in _jobs) kv.Value.TrySetException(new SteamException(3, "disconnected"));
	}

	public void Disconnect()
	{
		var ws = _ws;
		_ws = null;
		_recvCts?.Cancel();
		OnDisconnected();
		if (ws == null) return;
		try { ws.Abort(); } catch { }
		ws.Dispose();
	}

	public void Dispose() => Disconnect();

	// SteamKit2-compatible machine id: binary KeyValues "MessageObject" { BB3, FF2, 3B3 } of SHA1 hex strings.
	public static byte[] BuildMachineId(string seed)
	{
		static string H(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
		using var ms = new MemoryStream();
		void Str(string s) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b); ms.WriteByte(0); }
		ms.WriteByte(0); Str("MessageObject");
		foreach (var (k, v) in new[] { ("BB3", H(seed + "-guid")), ("FF2", H(seed + "-mac")), ("3B3", H(seed + "-disk")) })
		{
			ms.WriteByte(1); Str(k); Str(v);
		}
		ms.WriteByte(8);
		ms.WriteByte(8);
		return ms.ToArray();
	}
}
