#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using PortSteam;

namespace PortCloudSaves;

// Logged-in Steam CM connection for one account; reconnects on demand (iOS kills sockets in the background).
sealed class SteamSession
{
	public readonly Credentials Creds;
	public readonly SteamCm Cm = new();
	readonly SemaphoreSlim _lock = new(1, 1);

	public SteamSession(Credentials creds) => Creds = creds;

	public async Task<SteamCm> EnsureAsync(CancellationToken ct = default)
	{
		if (Cm.IsLoggedOn) return Cm;
		await _lock.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			for (int attempt = 0; ; attempt++)
			{
				if (Cm.IsLoggedOn) return Cm;
				try
				{
					await Cm.ConnectAsync(ct).ConfigureAwait(false);
					await Cm.LogOnAsync(Creds.AccountName, Creds.RefreshToken, Creds.MachineId, Credentials.DeviceName, ct).ConfigureAwait(false);
					Creds.SteamId = Cm.SteamId;
					PortCloud.Log($"logged on as {Creds.AccountName} ({Cm.SteamId})");
					return Cm;
				}
				catch (SteamException e) when (!e.IsAuthFailure && attempt < 2)
				{
					PortCloud.Log("logon retry: " + e.Message);
				}
			}
		}
		finally
		{
			_lock.Release();
		}
	}
}

// ICloudSaveStore backed by Steam Cloud (Cloud.* unified messages). Cloud metadata is cached after one
// enumeration; writes upload in the background. A per-file "last synced" state lets the next launch push
// changes that never reached Steam (offline play) instead of the game's cloud-wins sync discarding them.
sealed class PortCloudStore : ICloudSaveStore
{
	static readonly Regex Scope = new(@"^(profile\.save|profile[1-3]/saves/(progress|prefs|current_run|current_run_mp)\.save|profile[1-3]/saves/history/[^/]+\.run)$");

	readonly string _root;       // absolute path of the account-scoped local save dir
	readonly string _rootId;     // its user:// path; iOS moves the app container (and _root) on every reinstall
	readonly string _stateDir;   // absolute path of user://port_cloud
	readonly object _lock = new();
	readonly Dictionary<string, CloudFile> _cloud = new(StringComparer.Ordinal);
	readonly Dictionary<string, byte[]> _prefetched = new(StringComparer.Ordinal);
	Dictionary<string, long> _synced = new(StringComparer.Ordinal);
	readonly Dictionary<string, Op> _pending = new(StringComparer.Ordinal);
	bool _firstSync = true;
	bool _workerRunning;
	volatile bool _paused; // manual pull/push in progress: the upload worker stands down
	int _batchDepth;
	int _inFlight;

	public SteamSession? Session;
	public volatile bool Online; // cloud listing loaded and uploads allowed this session
	public long LastSyncUnix;     // last moment local and Steam Cloud were known to match
	public volatile string? LastError;

	public int PendingCount { get { lock (_lock) return _pending.Count + _inFlight; } }
	public int CloudFileCount { get { lock (_lock) return _cloud.Keys.Count(k => Scope.IsMatch(k)); } }

	sealed record Op(string Path, byte[]? Data, long Ts, long? PrevCloudTs);

	public PortCloudStore(string localRoot, string rootId, string stateDir)
	{
		_root = localRoot;
		_rootId = rootId;
		_stateDir = stateDir;
		Directory.CreateDirectory(_stateDir);
	}

	static string Canon(string path) => path.Replace("user://", "").Replace('\\', '/');
	static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
	string Abs(string path) => Path.Combine(_root, path);
	string StatePath => Path.Combine(_stateDir, "state.json");

	// ---- startup sync (called by PortCloud before the game's own SyncCloudToLocal) ----

	public async Task GoOnlineAsync(CancellationToken ct)
	{
		var cm = await Session!.EnsureAsync(ct).ConfigureAwait(false);
		var files = await SteamApi.EnumerateAsync(cm, ct).ConfigureAwait(false);
		lock (_lock)
		{
			_cloud.Clear();
			foreach (var f in files) _cloud[f.Name] = f;
		}
		LoadState();
		PortCloud.Log($"cloud has {files.Count} files ({files.Count(f => Scope.IsMatch(f.Name))} save files); " +
			(_firstSync ? "first sync for this account: Steam Cloud is authoritative" : $"{_synced.Count} files tracked"));
	}

	Dictionary<string, long> LocalScope()
	{
		var result = new Dictionary<string, long>(StringComparer.Ordinal);
		void Add(string rel)
		{
			var abs = Abs(rel);
			if (File.Exists(abs)) result[rel] = new DateTimeOffset(File.GetLastWriteTimeUtc(abs)).ToUnixTimeSeconds();
		}
		Add(ProfileSaveManager.ProfilePath);
		for (int i = 1; i <= 3; i++)
		{
			Add(Canon(ProgressSaveManager.GetProgressPathForProfile(i)));
			Add(Canon(PrefsSaveManager.GetPrefsPath(i)));
			Add(Canon(RunSaveManager.GetRunSavePath(i, "current_run.save")));
			Add(Canon(RunSaveManager.GetRunSavePath(i, "current_run_mp.save")));
			var hist = Canon(RunHistorySaveManager.GetHistoryPath(i));
			if (Directory.Exists(Abs(hist)))
				foreach (var f in Directory.GetFiles(Abs(hist)))
					if (Scope.IsMatch($"{hist}/{Path.GetFileName(f)}")) Add($"{hist}/{Path.GetFileName(f)}");
		}
		return result;
	}

	// Pushes local changes made since the last successful sync, so the game's cloud-wins sync keeps them.
	public async Task ReconcileAsync(CancellationToken ct)
	{
		if (_firstSync) return;
		var local = LocalScope();
		Dictionary<string, CloudFile> cloud;
		lock (_lock) cloud = new Dictionary<string, CloudFile>(_cloud);
		var paths = new HashSet<string>(local.Keys, StringComparer.Ordinal);
		paths.UnionWith(_synced.Keys);
		paths.UnionWith(cloud.Keys.Where(k => Scope.IsMatch(k)));
		int pushed = 0, deleted = 0, conflicts = 0;
		foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
		{
			long? l = local.TryGetValue(path, out var lv) ? lv : null;
			long? s = _synced.TryGetValue(path, out var sv) ? sv : null;
			long? c = cloud.TryGetValue(path, out var cf) ? cf.Timestamp : null;
			if (l.HasValue && l == c) continue;
			if (l == s) continue; // unchanged locally; the game's sync pulls any cloud change
			if (c != s)
			{
				conflicts++;
				bool localWins = l.HasValue && (!c.HasValue || l > c);
				PortCloud.Log($"conflict {path}: local={l} cloud={c} lastSync={s} -> {(localWins ? "local" : "cloud")} wins");
				if (!localWins)
				{
					if (l.HasValue) BackupConflict(path, File.ReadAllBytes(Abs(path)), "local");
					continue;
				}
				if (c.HasValue)
				{
					var cm0 = await Session!.EnsureAsync(ct).ConfigureAwait(false);
					BackupConflict(path, await SteamApi.DownloadAsync(cm0, path, ct).ConfigureAwait(false), "cloud");
				}
			}
			var cm = await Session!.EnsureAsync(ct).ConfigureAwait(false);
			if (l.HasValue)
			{
				var data = await File.ReadAllBytesAsync(Abs(path), ct).ConfigureAwait(false);
				bool uploaded = await SteamApi.UploadAsync(cm, path, data, l.Value, 0, ct).ConfigureAwait(false);
				lock (_lock)
				{
					if (!uploaded && c.HasValue)
					{
						// Identical bytes were already in Steam Cloud: adopt its timestamp locally.
						File.SetLastWriteTimeUtc(Abs(path), DateTimeOffset.FromUnixTimeSeconds(c.Value).UtcDateTime);
						_synced[path] = c.Value;
					}
					else
					{
						_cloud[path] = new CloudFile { Name = path, Timestamp = l.Value, Size = data.Length };
						_synced[path] = l.Value;
					}
				}
				pushed++;
			}
			else if (c.HasValue)
			{
				await SteamApi.DeleteAsync(cm, path, 0, ct).ConfigureAwait(false);
				lock (_lock)
				{
					_cloud.Remove(path);
					_synced.Remove(path);
				}
				deleted++;
			}
			else
			{
				lock (_lock) _synced.Remove(path);
			}
		}
		if (pushed + deleted + conflicts > 0) PortCloud.Log($"reconcile: pushed {pushed}, deleted {deleted}, conflicts {conflicts}");
		SaveState();
	}

	long? LocalTs(string path) => File.Exists(Abs(path)) ? new DateTimeOffset(File.GetLastWriteTimeUtc(Abs(path))).ToUnixTimeSeconds() : null;

	void BackupConflict(string path, byte[] data, string side)
	{
		try
		{
			var dir = Path.Combine(_stateDir, "conflicts");
			Directory.CreateDirectory(dir);
			File.WriteAllBytes(Path.Combine(dir, $"{path.Replace('/', '_')}.{Now()}.{side}"), data);
			foreach (var old in Directory.GetFiles(dir).OrderByDescending(File.GetLastWriteTimeUtc).Skip(40)) File.Delete(old);
		}
		catch (Exception e) { PortCloud.Log("conflict backup failed: " + e.Message); }
	}

	// Downloads, in parallel, every cloud save the game's sync is about to request one by one.
	public async Task PrefetchAsync(Action<int, int> progress, CancellationToken ct)
	{
		var local = LocalScope();
		List<string> todo;
		lock (_lock)
			todo = _cloud.Values.Where(f => Scope.IsMatch(f.Name) && f.Size > 0 &&
				(!local.TryGetValue(f.Name, out var lt) || lt != f.Timestamp)).Select(f => f.Name).ToList();
		if (todo.Count == 0) return;
		var cm = await Session!.EnsureAsync(ct).ConfigureAwait(false);
		int done = 0;
		progress(0, todo.Count);
		await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (name, token) =>
		{
			try
			{
				var data = await SteamApi.DownloadAsync(cm, name, token).ConfigureAwait(false);
				lock (_lock) _prefetched[name] = data;
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				PortCloud.Log($"prefetch {name} failed: {e.Message}");
			}
			progress(Interlocked.Increment(ref done), todo.Count);
		}).ConfigureAwait(false);
		PortCloud.Log($"downloaded {_prefetched.Count}/{todo.Count} changed saves from Steam Cloud");
	}

	// After the game's sync: every file now identical on both sides becomes the new "last synced" baseline.
	public void SnapshotState()
	{
		var local = LocalScope();
		Dictionary<string, CloudFile> cloud;
		lock (_lock)
		{
			cloud = new Dictionary<string, CloudFile>(_cloud);
			_prefetched.Clear();
		}
		var paths = new HashSet<string>(local.Keys, StringComparer.Ordinal);
		paths.UnionWith(cloud.Keys.Where(k => Scope.IsMatch(k)));
		lock (_lock) paths.UnionWith(_synced.Keys);
		lock (_lock)
			foreach (var p in paths)
			{
				bool hasL = local.TryGetValue(p, out var l), hasC = cloud.TryGetValue(p, out var c);
				if (hasL && hasC && l == c!.Timestamp) _synced[p] = l;
				else if (!hasL && !hasC) _synced.Remove(p);
			}
		_firstSync = false;
		Interlocked.Exchange(ref LastSyncUnix, Now());
		SaveState();
	}

	void LoadState()
	{
		_firstSync = true;
		_synced = new Dictionary<string, long>(StringComparer.Ordinal);
		try
		{
			if (!File.Exists(StatePath)) return;
			using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
			var root = doc.RootElement;
			var savedRoot = root.GetProperty("root").GetString() ?? "";
			bool sameRoot = savedRoot == _rootId || savedRoot.EndsWith("/Documents/" + _rootId.Replace("user://", ""), StringComparison.Ordinal); // pre-fix absolute path
			if (root.GetProperty("steamId").GetString() != Session!.Creds.SteamId.ToString() || !sameRoot) return;
			foreach (var p in root.GetProperty("files").EnumerateObject()) _synced[p.Name] = p.Value.GetInt64();
			_firstSync = false;
		}
		catch (Exception e) { PortCloud.Log("state load failed (treating as first sync): " + e.Message); }
	}

	void SaveState()
	{
		try
		{
			Dictionary<string, long> copy;
			lock (_lock) copy = new Dictionary<string, long>(_synced);
			using var ms = new MemoryStream();
			using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
			{
				w.WriteStartObject();
				w.WriteString("steamId", Session?.Creds.SteamId.ToString() ?? "");
				w.WriteString("root", _rootId);
				w.WriteStartObject("files");
				foreach (var kv in copy.OrderBy(k => k.Key, StringComparer.Ordinal)) w.WriteNumber(kv.Key, kv.Value);
				w.WriteEndObject();
				w.WriteEndObject();
			}
			var tmp = StatePath + ".tmp";
			File.WriteAllBytes(tmp, ms.ToArray());
			File.Move(tmp, StatePath, true);
		}
		catch (Exception e) { PortCloud.Log("state save failed: " + e.Message); }
	}

	// ---- background upload queue ----

	void Enqueue(Op op)
	{
		lock (_lock)
		{
			_pending[op.Path] = op;
			if (!Online || _paused || _workerRunning) return;
			_workerRunning = true;
		}
		_ = Task.Run(Worker);
	}

	public void Kick()
	{
		lock (_lock)
		{
			if (!Online || _paused || _workerRunning || _pending.Count == 0) return;
			_workerRunning = true;
		}
		_ = Task.Run(Worker);
	}

	async Task Worker()
	{
		int backoff = 0;
		while (true)
		{
			for (int t = 0, wait = backoff > 0 ? backoff * 1000 : 500; t < wait && !_paused; t += 100)
				await Task.Delay(100).ConfigureAwait(false);
			for (int i = 0; i < 100 && Volatile.Read(ref _batchDepth) > 0; i++) await Task.Delay(100).ConfigureAwait(false);
			List<Op> ops;
			lock (_lock)
			{
				if (_pending.Count == 0 || !Online || _paused) { _workerRunning = false; return; }
				ops = _pending.Values.ToList();
				_pending.Clear();
				_inFlight = ops.Count;
			}
			var remaining = new List<Op>(ops);
			try
			{
				var cm = await Session!.EnsureAsync().ConfigureAwait(false);
				ulong batch = 0;
				try
				{
					batch = await SteamApi.BeginBatchAsync(cm, Credentials.DeviceName,
						ops.Where(o => o.Data != null).Select(o => o.Path), ops.Where(o => o.Data == null).Select(o => o.Path)).ConfigureAwait(false);
				}
				catch (SteamException e) when (e.Result != 3) { PortCloud.Log("upload batch not started: " + e.Message); }
				bool allOk = true;
				foreach (var op in ops)
				{
					try
					{
						await Perform(cm, op, batch).ConfigureAwait(false);
					}
					catch (SteamException e) when (e.Result != 3 && !e.IsAuthFailure)
					{
						allOk = false;
						PortCloud.Log($"cloud {(op.Data == null ? "delete" : "upload")} of {op.Path} rejected: {e.Message}");
					}
					remaining.Remove(op);
					Interlocked.Decrement(ref _inFlight);
				}
				if (batch != 0)
					try { await SteamApi.CompleteBatchAsync(cm, batch, allOk).ConfigureAwait(false); }
					catch (Exception e) { PortCloud.Log("completing upload batch failed: " + e.Message); }
				SaveState();
				if (allOk && PendingCount == 0) Interlocked.Exchange(ref LastSyncUnix, Now());
				backoff = 0;
			}
			catch (Exception e)
			{
				LastError = e.Message;
				if (e is SteamException { IsAuthFailure: true })
				{
					Online = false;
					PortCloud.Log("Steam rejected the saved sign-in; uploads stop until the next launch: " + e.Message);
				}
				lock (_lock)
				{
					foreach (var op in remaining)
						_pending.TryAdd(op.Path, op); // keep a newer write of the same file if one arrived
					_inFlight = 0;
				}
				backoff = Math.Clamp(backoff * 2, 2, 60);
				PortCloud.Log($"cloud upload failed ({e.Message}); retrying {remaining.Count} in {backoff}s");
			}
		}
	}

	async Task Perform(SteamCm cm, Op op, ulong batch)
	{
		if (op.Data == null)
		{
			await SteamApi.DeleteAsync(cm, op.Path, batch).ConfigureAwait(false);
			lock (_lock) _synced.Remove(op.Path);
			PortCloud.Log($"deleted {op.Path} from Steam Cloud");
			return;
		}
		bool uploaded = await SteamApi.UploadAsync(cm, op.Path, op.Data, op.Ts, batch).ConfigureAwait(false);
		lock (_lock)
		{
			bool newer = _pending.ContainsKey(op.Path);
			if (!uploaded && op.PrevCloudTs is long prev)
			{
				// Steam already had identical bytes, so its timestamp didn't move: match the local file to it.
				if (!newer)
				{
					try { File.SetLastWriteTimeUtc(Abs(op.Path), DateTimeOffset.FromUnixTimeSeconds(prev).UtcDateTime); } catch { }
					_cloud[op.Path] = new CloudFile { Name = op.Path, Timestamp = prev, Size = op.Data.Length };
				}
				_synced[op.Path] = prev;
			}
			else
			{
				_synced[op.Path] = op.Ts;
			}
		}
		PortCloud.Log($"uploaded {op.Path} ({op.Data.Length} bytes){(uploaded ? "" : " (unchanged)")}");
	}

	// ---- manual status / pull / push (main-menu Steam Cloud panel) ----

	public sealed class Comparison
	{
		public int Same, NewerOnSteam, NotUploaded, BothChanged, OnlyOnSteam, OnlyHere;
		public bool UpToDate => NewerOnSteam + NotUploaded + BothChanged + OnlyOnSteam + OnlyHere == 0;
	}

	static string Sha(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
	static bool SameSha(string? cloudSha, string localSha) => cloudSha != null && cloudSha.Equals(localSha, StringComparison.OrdinalIgnoreCase);

	Dictionary<string, (long Ts, string Sha)> LocalWithSha()
	{
		var result = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
		foreach (var (path, ts) in LocalScope()) result[path] = (ts, Sha(File.ReadAllBytes(Abs(path))));
		return result;
	}

	async Task<Dictionary<string, CloudFile>> RefreshCloudAsync(CancellationToken ct)
	{
		var cm = await Session!.EnsureAsync(ct).ConfigureAwait(false);
		var files = await SteamApi.EnumerateAsync(cm, ct).ConfigureAwait(false);
		lock (_lock)
		{
			_cloud.Clear();
			foreach (var f in files) _cloud[f.Name] = f;
			foreach (var op in _pending.Values) // writes not uploaded yet stay visible to the game
				if (op.Data != null) _cloud[op.Path] = new CloudFile { Name = op.Path, Timestamp = op.Ts, Size = op.Data.Length };
				else _cloud.Remove(op.Path);
			if (_synced.Count == 0 && _firstSync) LoadState();
		}
		return files.Where(f => Scope.IsMatch(f.Name)).ToDictionary(f => f.Name, StringComparer.Ordinal);
	}

	public async Task<Comparison> CompareAsync(CancellationToken ct)
	{
		var cloud = await RefreshCloudAsync(ct).ConfigureAwait(false);
		var local = LocalWithSha();
		Dictionary<string, long> synced;
		lock (_lock) synced = new Dictionary<string, long>(_synced);
		var r = new Comparison();
		foreach (var path in local.Keys.Union(cloud.Keys))
		{
			bool hasL = local.TryGetValue(path, out var l), hasC = cloud.TryGetValue(path, out var c);
			if (hasL && hasC && SameSha(c!.Sha, l.Sha)) { r.Same++; continue; }
			if (!hasL) { r.OnlyOnSteam++; continue; }
			if (!hasC) { r.OnlyHere++; continue; }
			bool localChanged = !synced.TryGetValue(path, out var s) || l.Ts != s;
			bool cloudChanged = !synced.TryGetValue(path, out var s2) || c!.Timestamp != s2;
			if (localChanged && cloudChanged) r.BothChanged++;
			else if (cloudChanged) r.NewerOnSteam++;
			else r.NotUploaded++;
		}
		if (r.UpToDate && PendingCount == 0) Interlocked.Exchange(ref LastSyncUnix, Now());
		PortCloud.Log($"compare: same {r.Same}, newer on Steam {r.NewerOnSteam}, not uploaded {r.NotUploaded}, both {r.BothChanged}, only Steam {r.OnlyOnSteam}, only here {r.OnlyHere}");
		return r;
	}

	// Stops background uploads and waits for the current batch; queued uploads are dropped because the manual
	// pull/push replaces them.
	public async Task PauseUploadsAsync()
	{
		_paused = true;
		for (int i = 0; i < 300; i++)
		{
			lock (_lock)
				if (!_workerRunning) { _pending.Clear(); return; }
			await Task.Delay(100).ConfigureAwait(false);
		}
		lock (_lock) _pending.Clear();
	}

	public void ResumeUploads()
	{
		_paused = false;
		Kick();
	}

	string NewBackupDir(string label)
	{
		var root = Path.Combine(_stateDir, "backups");
		Directory.CreateDirectory(root);
		foreach (var old in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Skip(4))
			try { Directory.Delete(old, true); } catch { }
		var dir = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{label}");
		Directory.CreateDirectory(dir);
		return dir;
	}

	static void WriteBackup(string dir, string path, byte[] data)
	{
		var target = Path.Combine(dir, path);
		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		File.WriteAllBytes(target, data);
	}

	void SetLocalTs(string path, long ts) => File.SetLastWriteTimeUtc(Abs(path), DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime);

	// Makes this device's saves identical to Steam Cloud. Returns the number of files changed locally.
	public async Task<int> PullAllAsync(Action<int, int> progress, CancellationToken ct)
	{
		var cloud = await RefreshCloudAsync(ct).ConfigureAwait(false);
		var local = LocalWithSha();
		var download = cloud.Values.Where(c => !(local.TryGetValue(c.Name, out var l) && SameSha(c.Sha, l.Sha))).Select(c => c.Name).ToList();
		var delete = local.Keys.Where(p => !cloud.ContainsKey(p)).ToList();
		if (download.Count + delete.Count > 0)
		{
			var backup = NewBackupDir("before-pull");
			foreach (var p in download.Where(local.ContainsKey).Concat(delete)) WriteBackup(backup, p, File.ReadAllBytes(Abs(p)));
		}
		var cm = await Session!.EnsureAsync(ct).ConfigureAwait(false);
		var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		int done = 0;
		progress(0, download.Count);
		await Parallel.ForEachAsync(download, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (name, token) =>
		{
			var bytes = await SteamApi.DownloadAsync(cm, name, token).ConfigureAwait(false);
			lock (data) data[name] = bytes;
			progress(Interlocked.Increment(ref done), download.Count);
		}).ConfigureAwait(false);
		// Everything downloaded: only now touch local files.
		foreach (var (name, bytes) in data)
		{
			var abs = Abs(name);
			Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
			var tmp = abs + ".port_tmp";
			File.WriteAllBytes(tmp, bytes);
			File.Move(tmp, abs, true);
		}
		foreach (var p in delete)
		{
			File.Delete(Abs(p));
			if (File.Exists(Abs(p) + ".backup")) File.Delete(Abs(p) + ".backup");
		}
		lock (_lock)
		{
			foreach (var c in cloud.Values)
			{
				SetLocalTs(c.Name, c.Timestamp);
				_synced[c.Name] = c.Timestamp;
			}
			foreach (var p in delete) _synced.Remove(p);
			_firstSync = false;
		}
		SaveState();
		Interlocked.Exchange(ref LastSyncUnix, Now());
		Online = true;
		PortCloud.Log($"pull: {data.Count} downloaded, {delete.Count} deleted, {cloud.Count - data.Count} already identical");
		return data.Count + delete.Count;
	}

	// Makes Steam Cloud identical to this device's saves. Returns the number of files changed in Steam Cloud.
	public async Task<int> PushAllAsync(Action<int, int> progress, CancellationToken ct)
	{
		var cloud = await RefreshCloudAsync(ct).ConfigureAwait(false);
		var local = LocalWithSha();
		var upload = local.Keys.Where(p => !(cloud.TryGetValue(p, out var c) && SameSha(c.Sha, local[p].Sha))).ToList();
		var delete = cloud.Keys.Where(p => !local.ContainsKey(p)).ToList();
		var cm = await Session!.EnsureAsync(ct).ConfigureAwait(false);
		var replaced = upload.Where(cloud.ContainsKey).Concat(delete).ToList();
		if (replaced.Count > 0)
		{
			// Keep a copy of every Steam Cloud save this push replaces or deletes.
			var backup = NewBackupDir("before-push");
			await Parallel.ForEachAsync(replaced, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (name, token) =>
				WriteBackup(backup, name, await SteamApi.DownloadAsync(cm, name, token).ConfigureAwait(false))).ConfigureAwait(false);
		}
		int total = upload.Count + delete.Count, done = 0;
		progress(0, total);
		ulong batch = 0;
		if (total > 0)
			try { batch = await SteamApi.BeginBatchAsync(cm, Credentials.DeviceName, upload, delete, ct).ConfigureAwait(false); }
			catch (SteamException e) when (e.Result != 3) { PortCloud.Log("push batch not started: " + e.Message); }
		bool ok = false;
		try
		{
			foreach (var p in upload)
			{
				var bytes = await File.ReadAllBytesAsync(Abs(p), ct).ConfigureAwait(false);
				long ts = local[p].Ts;
				bool uploaded = await SteamApi.UploadAsync(cm, p, bytes, ts, batch, ct).ConfigureAwait(false);
				lock (_lock)
				{
					if (!uploaded && cloud.TryGetValue(p, out var c)) { SetLocalTs(p, c.Timestamp); ts = c.Timestamp; }
					_cloud[p] = new CloudFile { Name = p, Timestamp = ts, Size = bytes.Length, Sha = local[p].Sha };
					_synced[p] = ts;
				}
				progress(++done, total);
			}
			foreach (var p in delete)
			{
				await SteamApi.DeleteAsync(cm, p, batch, ct).ConfigureAwait(false);
				lock (_lock) { _cloud.Remove(p); _synced.Remove(p); }
				progress(++done, total);
			}
			ok = true;
		}
		finally
		{
			if (batch != 0)
				try { await SteamApi.CompleteBatchAsync(cm, batch, ok, CancellationToken.None).ConfigureAwait(false); }
				catch (Exception e) { PortCloud.Log("completing push batch failed: " + e.Message); }
			SaveState();
		}
		lock (_lock)
		{
			foreach (var p in local.Keys.Where(p => !upload.Contains(p)))
			{
				// Already identical: line local timestamps up with Steam's so the next launch sees no change.
				SetLocalTs(p, cloud[p].Timestamp);
				_synced[p] = cloud[p].Timestamp;
			}
			_firstSync = false;
		}
		SaveState();
		Interlocked.Exchange(ref LastSyncUnix, Now());
		Online = true;
		PortCloud.Log($"push: {upload.Count} uploaded, {delete.Count} deleted, {local.Count - upload.Count} already identical");
		return total;
	}

	// ---- ICloudSaveStore ----

	public string? ReadFile(string path) => Task.Run(() => ReadFileAsync(path)).GetAwaiter().GetResult();

	public async Task<string?> ReadFileAsync(string path)
	{
		path = Canon(path);
		lock (_lock)
		{
			if (_prefetched.Remove(path, out var cached)) return Encoding.UTF8.GetString(cached);
			if (_cloud.TryGetValue(path, out var f) && f.Size == 0) return string.Empty;
		}
		if (Session == null) throw new IOException("not signed in to Steam");
		var cm = await Session.EnsureAsync().ConfigureAwait(false);
		return Encoding.UTF8.GetString(await SteamApi.DownloadAsync(cm, path).ConfigureAwait(false));
	}

	public void WriteFile(string path, string content) => WriteFile(path, Encoding.UTF8.GetBytes(content));

	public void WriteFile(string path, byte[] content)
	{
		path = Canon(path);
		long ts = Now();
		long? prev;
		lock (_lock)
		{
			prev = _cloud.TryGetValue(path, out var old) ? old.Timestamp : null;
			_cloud[path] = new CloudFile { Name = path, Timestamp = ts, Size = content.Length };
		}
		Enqueue(new Op(path, content, ts, prev));
	}

	public Task WriteFileAsync(string path, string content) { WriteFile(path, content); return Task.CompletedTask; }
	public Task WriteFileAsync(string path, byte[] content) { WriteFile(path, content); return Task.CompletedTask; }

	public bool FileExists(string path) { lock (_lock) return _cloud.ContainsKey(Canon(path)); }
	public bool DirectoryExists(string path) => true;

	public void DeleteFile(string path)
	{
		path = Canon(path);
		bool existed;
		lock (_lock) existed = _cloud.Remove(path);
		if (existed) Enqueue(new Op(path, null, Now(), null));
	}

	public void RenameFile(string sourcePath, string destinationPath)
	{
		var dst = Canon(destinationPath);
		var abs = Abs(dst);
		if (File.Exists(abs))
		{
			long ts = new DateTimeOffset(File.GetLastWriteTimeUtc(abs)).ToUnixTimeSeconds();
			var data = File.ReadAllBytes(abs);
			lock (_lock) _cloud[dst] = new CloudFile { Name = dst, Timestamp = ts, Size = data.Length };
			Enqueue(new Op(dst, data, ts, null));
		}
		DeleteFile(sourcePath);
	}

	public string[] GetFilesInDirectory(string directoryPath)
	{
		var prefix = Canon(directoryPath).TrimEnd('/') + "/";
		lock (_lock)
			return _cloud.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) < 0)
				.Select(k => k.Substring(prefix.Length)).ToArray();
	}

	public string[] GetDirectoriesInDirectory(string directoryPath)
	{
		var prefix = Canon(directoryPath).TrimEnd('/') + "/";
		lock (_lock)
			return _cloud.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) > 0)
				.Select(k => k.Substring(prefix.Length, k.IndexOf('/', prefix.Length) - prefix.Length)).Distinct().ToArray();
	}

	public void CreateDirectory(string directoryPath) { }
	public void DeleteDirectory(string directoryPath) { }
	public void DeleteTemporaryFiles(string directoryPath) { }

	public DateTimeOffset GetLastModifiedTime(string path)
	{
		path = Canon(path);
		lock (_lock)
			if (_cloud.TryGetValue(path, out var f)) return DateTimeOffset.FromUnixTimeSeconds(f.Timestamp);
		return LocalTs(path) is long l ? DateTimeOffset.FromUnixTimeSeconds(l) : DateTimeOffset.UtcNow;
	}

	public int GetFileSize(string path) { lock (_lock) return _cloud.TryGetValue(Canon(path), out var f) ? f.Size : 0; }
	public void SetLastModifiedTime(string path, DateTimeOffset time) { }
	public string GetFullPath(string filename) => Canon(filename);
	public bool HasCloudFiles() { lock (_lock) return !Online || _cloud.Count > 0; }
	public void ForgetFile(string path) { } // keep history in Steam Cloud (500 MB quota) instead of Steam's local-only "forget"
	public bool IsFilePersisted(string path) => FileExists(path);
	public void BeginSaveBatch() => Interlocked.Increment(ref _batchDepth);
	public void EndSaveBatch() { if (Interlocked.Decrement(ref _batchDepth) < 0) Interlocked.Exchange(ref _batchDepth, 0); }
	public bool HasUserEnabledCloudSync() => true;
}
