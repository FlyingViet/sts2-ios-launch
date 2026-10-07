#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Port;
using PortSteam;

namespace PortCloudSaves;

// iOS port: Steam Cloud saves without Steamworks. sts2.dll is IL-patched (tools/patcher) so that
// SaveManager.ConstructDefault and NGame.DoCloudSync defer to the hooks installed here.
static class PortCloud
{
	internal static PortCloudStore? Store;
	static Node? _ui;
	static readonly Queue<(string Kind, string A, string B)> _events = new();
	static TaskCompletionSource<bool>? _wake;
	static bool _prefetching; // main thread only: gates progress toasts posted from download threads
	static bool _syncing;     // startup sync running
	static string? _account;  // shown in the main-menu panel; set once signed in
	static volatile string _busy = "", _result = "";
	static PortCloudStore.Comparison? _compare;
	static long _compareUnix;

	public static void Log(string msg) => GD.Print("[CLOUD] " + msg);

	[ModuleInitializer]
	internal static void Init()
	{
		PortHooks.ConstructSaveManager = ConstructSaveManager;
		PortHooks.CloudSync = CloudSync;
		SteamCm.Log = s => GD.Print(s);
	}

	static SaveManager ConstructSaveManager()
	{
		var basePath = UserDataPathProvider.GetAccountScopedBasePath(null);
		var local = new GodotFileIo(basePath);
		try
		{
			var creds = Credentials.Load();
			if (creds != null)
			{
				PortHooks.SteamId = creds.SteamId;
				PortHooks.PersonaName = creds.PersonaName;
			}
			Store = new PortCloudStore(ProjectSettings.GlobalizePath(basePath), basePath, ProjectSettings.GlobalizePath("user://port_cloud"));
			_account = creds == null ? null : creds.PersonaName ?? creds.AccountName;
			// res://port/steam_cloud_menu.gd (main-menu Steam Cloud panel) reads status and starts actions through these.
			Engine.Singleton.SetMeta("port_cloud_status", Callable.From(GetStatus));
			Engine.Singleton.SetMeta("port_cloud_action", Callable.From<string, bool>(StartAction));
			Log($"save store {basePath} + Steam Cloud ({(creds != null ? "signed in as " + creds.AccountName : "not signed in")})");
			return new SaveManager(new CloudSaveStore(local, Store), local);
		}
		catch (Exception e)
		{
			GD.PrintErr("[CLOUD] cloud store setup failed, saves stay local: " + e);
			return new SaveManager(local);
		}
	}

	// Replaces NGame.DoCloudSync; GameStartup waits for this task before loading the profile.
	static async Task CloudSync()
	{
		var store = Store;
		if (store == null) return;
		bool gameSyncStarted = false;
		_syncing = true;
		try
		{
			await SyncCore(store, () => gameSyncStarted = true);
		}
		catch (Exception e)
		{
			GD.PrintErr("[CLOUD] sync failed: " + e);
			store.LastError = e.Message;
			if (!gameSyncStarted) store.Online = false;
			Toast("Steam Cloud sync failed. Your progress will sync next launch.", 6);
		}
		finally
		{
			_syncing = false;
		}
	}

	static async Task SyncCore(PortCloudStore store, Action markGameSync)
	{
		var creds = Credentials.Load();
		string? prompt = null;
		for (int round = 0; ; round++)
		{
			if (creds == null)
			{
				creds = await SignInAsync(prompt);
				if (creds == null)
				{
					Log("sign-in skipped; saves stay local this session");
					CloseUi();
					return;
				}
			}
			store.Session = new SteamSession(creds);
			Toast("Connecting to Steam Cloud…", 0);
			try
			{
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
				await Task.Run(() => store.GoOnlineAsync(cts.Token));
				break;
			}
			catch (SteamException e) when (e.IsAuthFailure && round == 0)
			{
				Log("saved Steam sign-in rejected: " + e.Message);
				Credentials.Clear();
				store.Session = null;
				creds = null;
				prompt = "Your Steam sign-in expired. Sign in again to keep your saves synced with Steam Cloud.";
			}
			catch (Exception e)
			{
				Log("Steam Cloud unreachable: " + e.Message);
				store.Session = null;
				Toast("Steam Cloud unavailable. Your progress will sync next launch.", 6);
				return;
			}
		}

		if (store.Session!.Cm.PersonaName is { Length: > 0 } persona) creds.PersonaName = persona;
		_account = creds.PersonaName ?? creds.AccountName;
		PortHooks.SteamId = creds.SteamId;
		PortHooks.PersonaName = creds.PersonaName;
		creds.Save();

		using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
		{
			await Task.Run(() => store.ReconcileAsync(cts.Token));
			// Progress is posted from download threads with CallDeferred, which can run after the game's sync (and the
			// final toast) when that sync completes within one frame; drop any update that arrives late.
			_prefetching = true;
			try
			{
				await Task.Run(() => store.PrefetchAsync((done, total) =>
					Callable.From(() => { if (_prefetching) Toast($"Downloading saves from Steam Cloud… {done}/{total}", 0); }).CallDeferred(),
					cts.Token));
			}
			finally
			{
				_prefetching = false;
			}
		}

		store.Online = true;
		markGameSync();
		var sm = SaveManager.Instance;
		if (sm.ShouldOverwriteCloudWithLocal())
		{
			Log("Steam Cloud has no saves yet: uploading local saves");
			await sm.OverwriteCloudWithLocal();
		}
		else
		{
			await sm.SyncCloudToLocal();
		}
		store.SnapshotState();
		store.Kick();
		Log("sync complete");
		Toast(creds.PersonaName is { Length: > 0 } n ? $"Steam Cloud synced ({n})" : "Steam Cloud synced", 3);
	}

	static async Task<Credentials?> SignInAsync(string? prompt)
	{
		EnsureUi();
		var cm = new SteamCm();
		try
		{
			string message = prompt ?? "Sign in to Steam to sync your saves with Steam Cloud. Your password is sent only to Steam; " +
				"this iPhone keeps a sign-in token in the Keychain.";
			string lastAccount = "";
			while (true)
			{
				_ui!.Call("show_login", message, lastAccount);
				var ev = await NextEventAsync();
				if (ev.Kind == "skip") return null;
				if (ev.Kind != "login") continue;
				lastAccount = ev.A;
				_ui.Call("show_busy", "Signing in to Steam…");
				AuthSession session;
				try
				{
					if (!cm.IsConnected) await cm.ConnectAsync();
					session = await SteamApi.BeginAuthViaCredentialsAsync(cm, ev.A, ev.B, null, Credentials.DeviceName);
				}
				catch (SteamException e) when (e.Result == 5)
				{
					message = "That account name or password is incorrect.";
					continue;
				}
				catch (SteamException e) when (e.Result == 84)
				{
					message = "Too many sign-in attempts. Wait a few minutes, then try again.";
					continue;
				}
				catch (Exception e)
				{
					Log("sign-in failed: " + e);
					cm.Disconnect();
					message = $"Couldn't reach Steam ({e.Message}). Check your connection and try again.";
					continue;
				}
				var tokens = await CompleteGuardAsync(cm, session);
				if (tokens == null)
				{
					message = "Sign-in was cancelled or timed out. Sign in again, or tap Not now to play with local saves.";
					continue;
				}
				var creds = new Credentials
				{
					AccountName = tokens.AccountName.Length > 0 ? tokens.AccountName : ev.A,
					RefreshToken = tokens.RefreshToken,
					SteamId = session.SteamId,
					GuardData = tokens.GuardData,
					MachineSeed = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
				};
				creds.Save();
				Log($"signed in as {creds.AccountName} ({creds.SteamId})");
				return creds;
			}
		}
		finally
		{
			cm.Dispose();
		}
	}

	static async Task<AuthTokens?> CompleteGuardAsync(SteamCm cm, AuthSession s)
	{
		var types = s.Confirmations.Select(c => c.Type).ToList();
		bool deviceCode = types.Contains(GuardType.DeviceCode), emailCode = types.Contains(GuardType.EmailCode);
		bool confirm = types.Contains(GuardType.DeviceConfirmation) || types.Contains(GuardType.EmailConfirmation);
		var codeType = deviceCode ? GuardType.DeviceCode : GuardType.EmailCode;
		string email = s.Confirmations.FirstOrDefault(c => c.Type == GuardType.EmailCode).Message ?? "";
		string prompt =
			deviceCode && types.Contains(GuardType.DeviceConfirmation) ? "Approve this sign-in in the Steam Mobile App, or enter the Steam Guard code it shows." :
			deviceCode ? "Enter the Steam Guard code from the Steam Mobile App." :
			emailCode ? $"Enter the code Steam emailed to {(email.Length > 0 ? email : "you")}." :
			types.Contains(GuardType.EmailConfirmation) ? "Open the email from Steam and confirm this sign-in." :
			"Approve this sign-in in the Steam Mobile App.";
		Log("Steam Guard: " + string.Join(",", types));
		if (deviceCode || emailCode || confirm) _ui!.Call("show_code", prompt, deviceCode || emailCode);
		var deadline = DateTime.UtcNow.AddMinutes(5);
		while (DateTime.UtcNow < deadline)
		{
			var ev = await NextEventAsync(TimeSpan.FromSeconds(Math.Clamp(s.Interval, 1f, 10f)));
			if (ev.Kind is "cancel" or "skip") return null;
			if (ev.Kind == "code")
			{
				_ui!.Call("show_busy", "Checking code…");
				try
				{
					if (!cm.IsConnected) await cm.ConnectAsync();
					await SteamApi.SubmitGuardCodeAsync(cm, s, ev.A, codeType);
				}
				catch (SteamException e) when (e.Result is 65 or 88)
				{
					_ui.Call("show_code", "That code didn't work. Check it and try again.", true);
					continue;
				}
				catch (Exception e)
				{
					Log("Steam Guard code submit failed: " + e.Message);
					_ui.Call("show_code", $"Couldn't send the code ({e.Message}). Try again.", true);
					continue;
				}
			}
			try
			{
				if (!cm.IsConnected) await cm.ConnectAsync(); // the socket dies while the user is in the Steam app
				var tokens = await SteamApi.PollAuthAsync(cm, s);
				if (tokens != null) return tokens;
			}
			catch (SteamException e) when (e.Result != 3)
			{
				Log("sign-in session ended: " + e.Message);
				return null;
			}
			catch (Exception e)
			{
				Log("sign-in poll failed, retrying: " + e.Message);
			}
		}
		return null;
	}

	// ---- main-menu Steam Cloud panel (res://port/steam_cloud_menu.gd) ----

	static Godot.Collections.Dictionary GetStatus()
	{
		var store = Store;
		var d = new Godot.Collections.Dictionary
		{
			["signed_in"] = _account != null,
			["account"] = _account ?? "",
			["online"] = store?.Online ?? false,
			["syncing"] = _syncing,
			["busy"] = _busy,
			["result"] = _result,
			["last_sync"] = store?.LastSyncUnix ?? 0,
			["pending"] = store?.PendingCount ?? 0,
			["cloud_files"] = store?.CloudFileCount ?? 0,
			["last_error"] = store?.LastError ?? "",
			["can_transfer"] = store != null && _account != null && !_syncing && _busy.Length == 0,
		};
		if (_compare is { } c)
			d["compare"] = new Godot.Collections.Dictionary
			{
				["same"] = c.Same, ["newer_on_steam"] = c.NewerOnSteam, ["not_uploaded"] = c.NotUploaded,
				["both_changed"] = c.BothChanged, ["only_on_steam"] = c.OnlyOnSteam, ["only_here"] = c.OnlyHere,
				["up_to_date"] = c.UpToDate, ["checked"] = _compareUnix,
			};
		return d;
	}

	static bool StartAction(string action)
	{
		if (Store == null || _syncing || _busy.Length > 0 || action is not ("check" or "pull" or "push")) return false;
		if (action == "check") _result = "";
		_busy = action switch { "pull" => "Pulling from Steam Cloud…", "push" => "Pushing to Steam Cloud…", _ => "Checking Steam Cloud…" };
		_ = RunAction(Store, action);
		return true;
	}

	static async Task RunAction(PortCloudStore store, string action)
	{
		try
		{
			if (store.Session == null)
			{
				// Offline at launch (or signed in later): connect now with the saved sign-in.
				var creds = Credentials.Load();
				if (creds == null)
				{
					_result = "Not signed in. Close and reopen the game to sign in to Steam.";
					return;
				}
				store.Session = new SteamSession(creds);
			}
			using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			if (action is "pull" or "push")
			{
				await store.PauseUploadsAsync();
				try
				{
					Action<int, int> progress = (done, total) =>
						_busy = $"{(action == "pull" ? "Downloading from" : "Uploading to")} Steam Cloud… {done}/{total}";
					int changed = action == "pull"
						? await Task.Run(() => store.PullAllAsync(progress, cts.Token))
						: await Task.Run(() => store.PushAllAsync(progress, cts.Token));
					if (action == "pull" && changed > 0) ReloadGameData();
					_result = changed == 0 ? "Already identical to Steam Cloud. Nothing to change."
						: action == "pull" ? $"Pulled {changed} file(s) from Steam Cloud. Your previous iPhone saves were backed up on this iPhone."
						: $"Pushed {changed} file(s) to Steam Cloud. The Steam Cloud saves it replaced were backed up on this iPhone.";
				}
				finally
				{
					store.ResumeUploads();
				}
			}
			_busy = "Checking Steam Cloud…";
			_compare = await Task.Run(() => store.CompareAsync(cts.Token));
			_compareUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			store.LastError = null;
		}
		catch (SteamException e) when (e.IsAuthFailure)
		{
			Log("manual " + action + ": sign-in rejected: " + e.Message);
			Credentials.Clear();
			store.Session = null;
			store.Online = false;
			_account = null;
			_result = "Steam rejected the saved sign-in. Close and reopen the game to sign in again.";
		}
		catch (Exception e)
		{
			GD.PrintErr($"[CLOUD] manual {action} failed: {e}");
			store.LastError = e.Message;
			_result = $"{char.ToUpperInvariant(action[0])}{action[1..]} failed: {e.Message}";
		}
		finally
		{
			_busy = "";
		}
	}

	// Same reload the game's profile switcher does, so in-memory progress can't overwrite what was pulled.
	static void ReloadGameData()
	{
		var sm = SaveManager.Instance;
		sm.InitProfileId();
		var prefs = sm.InitPrefsData();
		var progress = sm.InitProgressData();
		var game = NGame.Instance;
		if (game?.MainMenu != null)
		{
			game.ReloadMainMenu();
			game.CheckShowSaveFileError(progress, prefs, null);
		}
		Log("reloaded saves after pull");
	}

	// ---- UI bridge (res://port/steam_cloud_ui.gd); everything here runs on the main thread ----

	static void Post(string kind, string a = "", string b = "")
	{
		_events.Enqueue((kind, a, b));
		_wake?.TrySetResult(true);
	}

	static async Task<(string Kind, string A, string B)> NextEventAsync(TimeSpan? timeout = null)
	{
		if (_events.Count > 0) return _events.Dequeue();
		_wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var wake = _wake.Task;
		if (timeout is { } t)
		{
			if (await Task.WhenAny(wake, Task.Delay(t)) != wake) return ("timeout", "", "");
		}
		else
		{
			await wake;
		}
		return _events.Count > 0 ? _events.Dequeue() : ("timeout", "", "");
	}

	static void EnsureUi()
	{
		if (_ui != null && GodotObject.IsInstanceValid(_ui)) return;
		_events.Clear();
		var script = GD.Load<GDScript>("res://port/steam_cloud_ui.gd");
		_ui = (Node)script.New().AsGodotObject();
		_ui.Connect("login_submitted", Callable.From<string, string>((a, p) => Post("login", a, p)));
		_ui.Connect("code_submitted", Callable.From<string>(c => Post("code", c)));
		_ui.Connect("skipped", Callable.From(() => Post("skip")));
		_ui.Connect("cancelled", Callable.From(() => Post("cancel")));
		((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, _ui);
	}

	static void Toast(string message, float seconds)
	{
		EnsureUi();
		_ui!.Call("show_toast", message, seconds);
	}

	static void CloseUi()
	{
		if (_ui != null && GodotObject.IsInstanceValid(_ui)) _ui.Call("close");
		_ui = null;
	}
}
