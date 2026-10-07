#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PortSteam;

public enum GuardType { Unknown = 0, None = 1, EmailCode = 2, DeviceCode = 3, DeviceConfirmation = 4, EmailConfirmation = 5, MachineToken = 6 }

public sealed class AuthSession
{
	public ulong ClientId;
	public byte[] RequestId = Array.Empty<byte>();
	public ulong SteamId;
	public float Interval = 5;
	public readonly List<(GuardType Type, string? Message)> Confirmations = new();
}

public sealed class AuthTokens
{
	public string AccountName = "", RefreshToken = "";
	public string? GuardData;
}

public sealed class CloudFile
{
	public string Name = "";
	public long Timestamp;
	public int Size;
	public string? Sha; // hex SHA-1 of the raw content (extended details)
}

// Steam's IAuthenticationService (credentials + Steam Guard) and ICloudService over a SteamCm connection.
public static class SteamApi
{
	public const uint AppId = 2868840; // Slay the Spire 2

	public static async Task<AuthSession> BeginAuthViaCredentialsAsync(SteamCm cm, string account, string password,
		string? guardData, string deviceName, CancellationToken ct = default)
	{
		var key = await cm.CallAsync("Authentication.GetPasswordRSAPublicKey#1", new ProtoWriter().Str(1, account), ct).ConfigureAwait(false);
		byte[] encrypted;
		using (var rsa = RSA.Create())
		{
			rsa.ImportParameters(new RSAParameters
			{
				Modulus = Convert.FromHexString(key.S(1) ?? throw new InvalidDataException("no RSA modulus")),
				Exponent = Convert.FromHexString(key.S(2) ?? throw new InvalidDataException("no RSA exponent")),
			});
			encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1);
		}
		var req = new ProtoWriter()
			.Str(2, account)
			.Str(3, Convert.ToBase64String(encrypted))
			.U64(4, key.U(3))
			.I32(7, 1) // persistent session
			.Str(8, "Client")
			.Msg(9, new ProtoWriter().Str(1, deviceName).I32(2, 1 /* SteamClient */).I32(3, SteamCm.OsType))
			.Str(10, string.IsNullOrEmpty(guardData) ? null : guardData);
		var r = await cm.CallAsync("Authentication.BeginAuthSessionViaCredentials#1", req, ct).ConfigureAwait(false);
		var s = new AuthSession { ClientId = r.U(1), RequestId = r.Bytes(2) ?? Array.Empty<byte>(), SteamId = r.U(5) };
		if (r.Has(3) && r.F32(3) > 0) s.Interval = r.F32(3);
		foreach (var c in r.Ms(4)) s.Confirmations.Add(((GuardType)c.I32(1), c.S(2)));
		return s;
	}

	public static Task SubmitGuardCodeAsync(SteamCm cm, AuthSession s, string code, GuardType type, CancellationToken ct = default) =>
		cm.CallAsync("Authentication.UpdateAuthSessionWithSteamGuardCode#1",
			new ProtoWriter().U64(1, s.ClientId).Fixed64(2, s.SteamId).Str(3, code).I32(4, (int)type), ct);

	// Returns tokens once the user has approved / entered a valid code, otherwise null.
	public static async Task<AuthTokens?> PollAuthAsync(SteamCm cm, AuthSession s, CancellationToken ct = default)
	{
		var r = await cm.CallAsync("Authentication.PollAuthSessionStatus#1",
			new ProtoWriter().U64(1, s.ClientId).Bytes(2, s.RequestId), ct).ConfigureAwait(false);
		if (r.U(1) != 0) s.ClientId = r.U(1);
		if (r.S(3) is not { Length: > 0 } refresh) return null;
		return new AuthTokens { RefreshToken = refresh, AccountName = r.S(6) ?? "", GuardData = r.S(7) };
	}

	public static async Task<List<CloudFile>> EnumerateAsync(SteamCm cm, CancellationToken ct = default)
	{
		var files = new List<CloudFile>();
		for (uint start = 0; ;)
		{
			var r = await cm.CallAsync("Cloud.EnumerateUserFiles#1",
				new ProtoWriter().U32(1, AppId).Bool(2, true).U32(3, 500).U32(4, start), ct).ConfigureAwait(false);
			int n = 0;
			foreach (var f in r.Ms(1))
			{
				n++;
				if (f.S(3) is { Length: > 0 } name)
					files.Add(new CloudFile { Name = name, Timestamp = (long)f.U(4), Size = (int)f.U(5), Sha = f.S(10) });
			}
			start += (uint)n;
			if (n == 0 || start >= r.U(2)) break;
		}
		return files;
	}

	public static async Task<byte[]> DownloadAsync(SteamCm cm, string name, CancellationToken ct = default)
	{
		var r = await cm.CallAsync("Cloud.ClientFileDownload#1", new ProtoWriter().U32(1, AppId).Str(2, name), ct).ConfigureAwait(false);
		if (r.S(7) is not { Length: > 0 } host) throw new IOException($"no download URL for {name}");
		if (r.B(11)) throw new IOException($"{name} is encrypted in Steam Cloud");
		using var req = new HttpRequestMessage(HttpMethod.Get, $"{(r.B(9) ? "https" : "http")}://{host}{r.S(8)}");
		foreach (var h in r.Ms(10))
			if (h.S(1) is { } hn) req.Headers.TryAddWithoutValidation(hn, h.S(2));
		using var resp = await SteamCm.Http.SendAsync(req, ct).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
		var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
		uint rawSize = (uint)r.U(3);
		// Steam stores some files zip-compressed (raw_file_size != file_size).
		if (rawSize != 0 && rawSize != data.Length && data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 3 && data[3] == 4)
		{
			using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
			using var es = zip.Entries[0].Open();
			using var outMs = new MemoryStream();
			await es.CopyToAsync(outMs, ct).ConfigureAwait(false);
			data = outMs.ToArray();
		}
		return data;
	}

	// Returns false if Steam already has identical content (DuplicateRequest).
	public static async Task<bool> UploadAsync(SteamCm cm, string name, byte[] data, long timestamp, ulong batchId, CancellationToken ct = default)
	{
		byte[] sha = SHA1.HashData(data);
		Task<ProtoMsg> Commit(bool ok) => cm.CallAsync("Cloud.ClientCommitFileUpload#1",
			new ProtoWriter().Bool(1, ok).U32(2, AppId).Bytes(3, sha).Str(4, name), ct);
		var begin = new ProtoWriter()
			.U32(1, AppId).U32(2, (uint)data.Length).U32(3, (uint)data.Length).Bytes(4, sha)
			.U64(5, (ulong)timestamp).Str(6, name).Bool(10, false).Bool(11, false);
		if (batchId != 0) begin.U64(13, batchId);
		ProtoMsg r;
		try
		{
			r = await cm.CallAsync("Cloud.ClientBeginFileUpload#1", begin, ct).ConfigureAwait(false);
		}
		catch (SteamException e) when (e.Result == 29)
		{
			return false;
		}
		try
		{
			foreach (var b in r.Ms(2))
			{
				var method = b.I32(4) switch { 1 => HttpMethod.Get, 3 => HttpMethod.Post, _ => HttpMethod.Put };
				using var req = new HttpRequestMessage(method, $"{(b.B(3) ? "https" : "http")}://{b.S(1)}{b.S(2)}");
				byte[] body = b.Bytes(8) is { Length: > 0 } explicitBody
					? explicitBody
					: data.AsSpan((int)b.U(6), (int)b.U(7)).ToArray();
				req.Content = new ByteArrayContent(body);
				req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
				foreach (var h in b.Ms(5))
				{
					if (h.S(1) is not { } hn || hn.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
					if (hn.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) req.Content.Headers.Remove(hn);
					if (!req.Headers.TryAddWithoutValidation(hn, h.S(2)))
						req.Content.Headers.TryAddWithoutValidation(hn, h.S(2));
				}
				using var resp = await SteamCm.Http.SendAsync(req, ct).ConfigureAwait(false);
				resp.EnsureSuccessStatusCode();
			}
		}
		catch
		{
			try { await Commit(false).ConfigureAwait(false); } catch { }
			throw;
		}
		if (!(await Commit(true).ConfigureAwait(false)).B(1)) throw new IOException($"Steam did not commit {name}");
		return true;
	}

	public static Task DeleteAsync(SteamCm cm, string name, ulong batchId, CancellationToken ct = default)
	{
		var w = new ProtoWriter().U32(1, AppId).Str(2, name);
		if (batchId != 0) w.U64(4, batchId);
		return cm.CallAsync("Cloud.ClientDeleteFile#1", w, ct);
	}

	public static async Task<ulong> BeginBatchAsync(SteamCm cm, string machineName, IEnumerable<string> uploads, IEnumerable<string> deletes, CancellationToken ct = default)
	{
		var w = new ProtoWriter().U32(1, AppId).Str(2, machineName);
		foreach (var u in uploads) w.Str(3, u);
		foreach (var d in deletes) w.Str(4, d);
		var r = await cm.CallAsync("Cloud.BeginAppUploadBatch#1", w, ct).ConfigureAwait(false);
		return r.U(1);
	}

	public static Task CompleteBatchAsync(SteamCm cm, ulong batchId, bool success, CancellationToken ct = default) =>
		cm.CallAsync("Cloud.CompleteAppUploadBatchBlocking#1",
			new ProtoWriter().U32(1, AppId).U64(2, batchId).U32(3, success ? 1u : 2u), ct);
}
