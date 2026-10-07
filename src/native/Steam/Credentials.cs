#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortSteam;

namespace PortCloudSaves;

sealed class Credentials
{
	public const string DeviceName = "iPhone - Slay the Spire 2 (iOS port)";
	public string AccountName = "", RefreshToken = "", MachineSeed = "";
	public string? GuardData, PersonaName;
	public ulong SteamId;

	public byte[] MachineId => SteamCm.BuildMachineId(MachineSeed);

	public static Credentials? Load()
	{
		try
		{
			var data = Keychain.Read();
			if (data == null) return null;
			using var doc = JsonDocument.Parse(data);
			var r = doc.RootElement;
			var c = new Credentials
			{
				AccountName = r.GetProperty("account").GetString() ?? "",
				RefreshToken = r.GetProperty("refresh").GetString() ?? "",
				MachineSeed = r.GetProperty("machine").GetString() ?? "",
				SteamId = ulong.Parse(r.GetProperty("steamid").GetString() ?? "0"),
				GuardData = r.TryGetProperty("guard", out var g) ? g.GetString() : null,
				PersonaName = r.TryGetProperty("persona", out var p) ? p.GetString() : null,
			};
			return c.RefreshToken.Length > 0 ? c : null;
		}
		catch (Exception e)
		{
			PortCloud.Log("reading saved Steam sign-in failed: " + e.Message);
			return null;
		}
	}

	public void Save()
	{
		if (MachineSeed.Length == 0) MachineSeed = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
		using var ms = new MemoryStream();
		using (var w = new Utf8JsonWriter(ms))
		{
			w.WriteStartObject();
			w.WriteString("account", AccountName);
			w.WriteString("refresh", RefreshToken);
			w.WriteString("machine", MachineSeed);
			w.WriteString("steamid", SteamId.ToString());
			if (GuardData != null) w.WriteString("guard", GuardData);
			if (PersonaName != null) w.WriteString("persona", PersonaName);
			w.WriteEndObject();
		}
		int status = Keychain.Write(ms.ToArray());
		if (status != 0) PortCloud.Log($"saving Steam sign-in to the Keychain failed (OSStatus {status})");
	}

	public static void Clear() => Keychain.Delete();
}

// Generic-password Keychain item, readable after first unlock and never synced off this device.
static unsafe class Keychain
{
	const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	const string Sec = "/System/Library/Frameworks/Security.framework/Security";
	const uint Utf8 = 0x08000100;
	const string Service = "com.flyingviet.sts2poc.steam-cloud", Account = "steam-refresh-token";

	[DllImport(CF)] static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte* s, uint encoding);
	[DllImport(CF)] static extern IntPtr CFDataCreate(IntPtr alloc, byte* bytes, nint length);
	[DllImport(CF)] static extern nint CFDataGetLength(IntPtr data);
	[DllImport(CF)] static extern byte* CFDataGetBytePtr(IntPtr data);
	[DllImport(CF)] static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);
	[DllImport(CF)] static extern void CFRelease(IntPtr cf);
	[DllImport(Sec)] static extern int SecItemAdd(IntPtr attributes, IntPtr* result);
	[DllImport(Sec)] static extern int SecItemCopyMatching(IntPtr query, IntPtr* result);
	[DllImport(Sec)] static extern int SecItemDelete(IntPtr query);

	static readonly IntPtr CfLib = NativeLibrary.Load(CF), SecLib = NativeLibrary.Load(Sec);
	static IntPtr Const(IntPtr lib, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));

	static IntPtr Str(string s)
	{
		var b = Encoding.UTF8.GetBytes(s + "\0");
		fixed (byte* p = b) return CFStringCreateWithCString(IntPtr.Zero, p, Utf8);
	}

	static IntPtr Dict(params (IntPtr key, IntPtr value)[] items)
	{
		var keys = stackalloc IntPtr[items.Length];
		var values = stackalloc IntPtr[items.Length];
		for (int i = 0; i < items.Length; i++) { keys[i] = items[i].key; values[i] = items[i].value; }
		return CFDictionaryCreate(IntPtr.Zero, keys, values, items.Length,
			NativeLibrary.GetExport(CfLib, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(CfLib, "kCFTypeDictionaryValueCallBacks"));
	}

	static (IntPtr, IntPtr)[] Query(IntPtr service, IntPtr account) => new[]
	{
		(Const(SecLib, "kSecClass"), Const(SecLib, "kSecClassGenericPassword")),
		(Const(SecLib, "kSecAttrService"), service),
		(Const(SecLib, "kSecAttrAccount"), account),
	};

	public static byte[]? Read()
	{
		IntPtr service = Str(Service), account = Str(Account), result = IntPtr.Zero;
		var q = Query(service, account);
		var dict = Dict(q[0], q[1], q[2],
			(Const(SecLib, "kSecReturnData"), Const(CfLib, "kCFBooleanTrue")),
			(Const(SecLib, "kSecMatchLimit"), Const(SecLib, "kSecMatchLimitOne")));
		try
		{
			int status = SecItemCopyMatching(dict, &result);
			if (status == -25300) return null; // errSecItemNotFound
			if (status != 0) throw new IOException($"SecItemCopyMatching OSStatus {status}");
			return new ReadOnlySpan<byte>(CFDataGetBytePtr(result), (int)CFDataGetLength(result)).ToArray();
		}
		finally
		{
			if (result != IntPtr.Zero) CFRelease(result);
			CFRelease(dict); CFRelease(service); CFRelease(account);
		}
	}

	public static int Write(byte[] data)
	{
		Delete();
		IntPtr service = Str(Service), account = Str(Account), cfData;
		fixed (byte* p = data) cfData = CFDataCreate(IntPtr.Zero, p, data.Length);
		var q = Query(service, account);
		var dict = Dict(q[0], q[1], q[2],
			(Const(SecLib, "kSecValueData"), cfData),
			(Const(SecLib, "kSecAttrAccessible"), Const(SecLib, "kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly")));
		try { return SecItemAdd(dict, null); }
		finally { CFRelease(dict); CFRelease(cfData); CFRelease(service); CFRelease(account); }
	}

	public static void Delete()
	{
		IntPtr service = Str(Service), account = Str(Account);
		var q = Query(service, account);
		var dict = Dict(q);
		try { SecItemDelete(dict); }
		finally { CFRelease(dict); CFRelease(service); CFRelease(account); }
	}
}
