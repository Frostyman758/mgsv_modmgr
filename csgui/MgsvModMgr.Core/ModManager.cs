using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MgsvModMgr.Core;

/// <summary>
/// Top-level orchestrator. Owns the persisted <see cref="State"/> and the
/// on-disk workspace that backs Apply / Revert.
///
/// <para>Workspace layout (sibling to the running exe):</para>
/// <list type="bullet">
///   <item><c>state.txt</c> — persisted settings + load order.</item>
///   <item><c>mods/</c>     — copies of every added <c>.mgsv</c> archive.</item>
///   <item><c>root/</c>     — pristine baseline cache. The first time the
///       manager needs to modify a game file, it copies the file in its
///       untouched state into here. Subsequent applies always rebuild from
///       this baseline; the file under <c>root/</c> is never modified
///       again. Files that did not exist pre-mod get a sentinel
///       <c>.absent</c> marker so revert removes them rather than
///       restoring stale data.</item>
///   <item><c>tmp/</c>      — scratch space used by Apply for unpack/repack.</item>
/// </list>
///
/// <para>Apply, for every host file an installed mod touches:</para>
/// <list type="number">
///   <item>Snapshot the live game file into <c>root/</c> if not already there.</item>
///   <item>Unpack the baseline into <c>tmp/</c>.</item>
///   <item>Overlay each enabled mod's contribution in load order.</item>
///   <item>Repack and write the result back to the game install.</item>
/// </list>
///
/// <para>Revert restores every file in <c>root/</c> back to its original
/// path in the game install and deletes anything the mods introduced.</para>
/// </summary>
public sealed class ModManager
{
    public ModManager(string? workspaceDir = null)
    {
        WorkspaceDir = workspaceDir ?? Path.GetDirectoryName(Environment.ProcessPath ?? ".") ?? ".";
        StatePath    = Path.Combine(WorkspaceDir, "state.txt");
        BaselineDir  = Path.Combine(WorkspaceDir, "root");
        ModsDir      = Path.Combine(WorkspaceDir, "mods");
        TmpDir       = Path.Combine(WorkspaceDir, "tmp");
    }

    /// <summary>Directory holding the manager's runtime tree (state/mods/root/tmp).</summary>
    public string WorkspaceDir { get; }
    public string StatePath    { get; }

    /// <summary>Pristine baseline cache. Files here are written exactly once and never touched again.</summary>
    public string BaselineDir  { get; }
    public string ModsDir      { get; }
    public string TmpDir       { get; }

    public State State { get; } = new();

    /// <summary>Sink for human-readable progress lines. Thread-safe — callers should marshal.</summary>
    public Action<string> Log { get; set; } = _ => { };

    /// <summary>
    /// Reports Apply progress as a fraction 0.0 .. 1.0 (1.0 = done). Called
    /// once per host completion. May fire from worker threads — marshal to
    /// the UI thread in the handler.
    /// </summary>
    public Action<double>? ApplyProgressed { get; set; }

    /// <summary>
    /// Max concurrent host rebuilds in <see cref="ApplyAll"/>. Each running
    /// rebuild spawns its own datfpk process. On SSD/NVMe 4–8 is a good
    /// range; on HDD use 1–2 to avoid head thrash. Default: half of logical
    /// processor count, clamped to [2, 8].
    /// </summary>
    public int ApplyParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    // ─── State I/O ─────────────────────────────────────────────────────────

    public void LoadState() => StateIo.Load(State, StatePath);
    public void SaveState() => StateIo.Save(State, StatePath);

    /// <summary>
    /// Set the MGSV install root + datfpk path, persist, and seed the two
    /// dictionary files into the game root from the baselines shipped next
    /// to the modmgr exe if they're missing.
    /// </summary>
    public void Init(string gameRoot, string datfpk)
    {
        State.GameRoot = Path.GetFullPath(gameRoot);
        State.DatFpk   = Path.GetFullPath(datfpk);
        SaveState();
        DictionaryWriter.EnsureBaselinesAt(State.GameRoot, WorkspaceDir, Log);
        Log($"Initialised. game_root={State.GameRoot}  datfpk={State.DatFpk}");
    }

    /// <summary>Where a mod's archive is unpacked for inspection (under <see cref="TmpDir"/>).</summary>
    public string ModUnpackDir(string id) => Path.Combine(TmpDir, "mod_" + id);

    // ─── Mod CRUD ──────────────────────────────────────────────────────────

    /// <summary>
    /// Register a <c>.mgsv</c> archive: copy it into the mods folder, extract
    /// it to <see cref="TmpDir"/>, parse its <c>metadata.xml</c>, and append
    /// its path entries to the game's PathDictionary files.
    /// </summary>
    public void AddMod(string mgsvPath)
    {
        EnsureInitialised();

        var src = Path.GetFullPath(mgsvPath);
        if (!File.Exists(src)) throw new FileNotFoundException(src);

        var id = Path.GetFileNameWithoutExtension(src).ToLowerInvariant();
        if (State.Mods.Any(m => m.Id == id))
            throw new InvalidOperationException("Mod id already registered: " + id);

        Directory.CreateDirectory(ModsDir);
        var stored = Path.Combine(ModsDir, id + ".mgsv");
        File.Copy(src, stored, overwrite: true);

        var unpacked = ModUnpackDir(id);
        if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true);
        ExtractZip(stored, unpacked);

        var mod = new ModInfo { Id = id, Enabled = true, Source = stored };
        var meta = Path.Combine(unpacked, "metadata.xml");
        if (File.Exists(meta)) MetadataParser.Parse(meta, mod);
        else                   Log("WARN: no metadata.xml in mod");

        ScanGameDir(unpacked, mod.GameDirEntries);
        mod.GameDirEntries.Sort(StringComparer.Ordinal);
        DeduplicateInPlace(mod.GameDirEntries);

        State.Mods.Add(mod);
        SaveState();

        var dictAdded = DictionaryWriter.UpdateFromMod(mod, State.GameRoot, WorkspaceDir, Log);
        Log($"Added: {mod.Id}  ({mod.Name} v{mod.Version})");
        Log($"  qar entries:     {mod.QarPaths.Count}");
        Log($"  fpk hosts:       {mod.FpkEntries.Count}");
        Log($"  gamedir entries: {mod.GameDirEntries.Count}");
        Log($"  dictionary +{dictAdded} new entries");
    }

    public void EnableMod(string id, bool enabled)
    {
        var m = FindMod(id);
        m.Enabled = enabled;
        m.Applied = false;   // game install no longer matches this mod's state
        SaveState();
    }

    public void RemoveMod(string id)
    {
        var mod = FindMod(id);
        if (File.Exists(mod.Source)) File.Delete(mod.Source);

        // Clean up the extracted tree so a future re-add starts from the
        // archive rather than a stale extraction.
        var unpacked = ModUnpackDir(id);
        if (Directory.Exists(unpacked))
        {
            try { Directory.Delete(unpacked, recursive: true); }
            catch (Exception ex) { Log($"WARN: could not delete {unpacked}: {ex.Message}"); }
        }

        State.Mods.Remove(mod);
        SaveState();
    }

    public void MoveMod(string id, int delta)
    {
        var index = IndexOfMod(id);
        MoveModToIndex(id, index + delta);
    }

    /// <summary>Move <paramref name="id"/> to <paramref name="newIndex"/> (clamped). No-op if it's already there.</summary>
    public void MoveModToIndex(string id, int newIndex)
    {
        var from = IndexOfMod(id);
        var to   = Math.Clamp(newIndex, 0, State.Mods.Count - 1);
        if (from == to) return;
        State.Mods.Move(from, to);

        // Reordering changes which mod wins overlapping files, so every mod's
        // merged contribution is potentially stale until the next Apply.
        foreach (var m in State.Mods) m.Applied = false;
        SaveState();
    }

    private int IndexOfMod(string id)
    {
        for (var i = 0; i < State.Mods.Count; i++)
            if (State.Mods[i].Id == id) return i;
        throw new InvalidOperationException("No such mod id: " + id);
    }

    /// <summary>
    /// Re-parse every installed mod's metadata and re-emit the PathDictionary
    /// files. Useful when seeding the dictionaries on an existing install or
    /// after upgrading the manager.
    /// </summary>
    /// <returns>Total number of newly-appended dictionary entries.</returns>
    public int RebuildDictionary()
    {
        if (string.IsNullOrEmpty(State.GameRoot))
            throw new InvalidOperationException("game_root is not set");

        var total = 0;
        foreach (var mod in State.Mods)
        {
            // Pick up Hash="" attributes that older state files didn't record.
            var meta = Path.Combine(ModUnpackDir(mod.Id), "metadata.xml");
            if (File.Exists(meta))
            {
                var tmp = new ModInfo { Id = mod.Id };
                MetadataParser.Parse(meta, tmp);
                foreach (var kv in tmp.QarHashes)
                    if (!string.IsNullOrEmpty(kv.Value)) mod.QarHashes.TryAdd(kv.Key, kv.Value);
            }
            total += DictionaryWriter.UpdateFromMod(mod, State.GameRoot, WorkspaceDir, Log);
        }
        SaveState();
        return total;
    }

    // ─── Apply / revert ────────────────────────────────────────────────────

    /// <summary>Rebuild every fpk host the installed mods touch, then overlay any GameDir loose files.</summary>
    public void ApplyAll()
    {
        EnsureInitialised();
        if (string.IsNullOrEmpty(State.DatFpk) || !File.Exists(State.DatFpk))
            throw new InvalidOperationException("datfpk not found: " + State.DatFpk);

        Directory.CreateDirectory(TmpDir);

        // mod_<id>/ extractions persist between Applies — they're the input
        // data, only the per-host work product is discardable. We DO NOT wipe
        // host_*/ scratch up-front any more, because most hosts are likely
        // cache hits and don't need touching. Hosts that actually rebuild
        // already wipe their own work dir before use inside RebuildFpkHost.

        var cache = new ApplyCache(Path.Combine(WorkspaceDir, "apply-cache.txt"));

        // Pre-extract every enabled mod's archive once. ModPayloadFor would
        // do this lazily otherwise, but lazy extraction under Parallel.ForEach
        // means two workers could race on the same target dir.
        foreach (var mod in State.Mods)
        {
            if (!mod.Enabled) continue;
            var unpacked = ModUnpackDir(mod.Id);
            if (!Directory.Exists(unpacked) && File.Exists(mod.Source))
                ExtractZip(mod.Source, unpacked);
        }

        var hosts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var mod in State.Mods)
            foreach (var qar in mod.QarPaths) hosts.Add(qar);

        int fpkCount = 0, rawCount = 0, skipped = 0, done = 0;
        var totalHosts = hosts.Count;

        // The host rebuilds are independent (each writes to its own work-dir
        // and its own disk path), so we Parallel.ForEach them. datfpk is
        // process-isolated so it has no shared state between invocations.
        // Cap concurrency via ApplyParallelism — single-drive I/O contention
        // makes wider settings counter-productive past ~8.
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ApplyParallelism) };
        Log($"Apply: {totalHosts} host(s) with up to {parallelOpts.MaxDegreeOfParallelism} worker(s).");
        ApplyProgressed?.Invoke(0);

        Parallel.ForEach(hosts, parallelOpts, qar =>
        {
            var disk = Paths.ResolveQar(qar, State.GameRoot);

            var contributors = new List<(ModInfo, string)>();
            foreach (var m in State.Mods)
                if (m.Enabled && m.QarPaths.Contains(qar))
                    contributors.Add((m, m.Source));

            EnsureBaseline(disk);
            var baseline    = BaselineFor(disk);
            var fingerprint = ApplyCache.Compute(qar, contributors, File.Exists(baseline) ? baseline : null);

            if (cache.Matches(disk, fingerprint) && File.Exists(disk))
            {
                Interlocked.Increment(ref skipped);
            }
            else
            {
                if (Paths.QarIsFpk(qar)) { RebuildFpkHost(qar, disk); Interlocked.Increment(ref fpkCount); }
                else                      { RebuildRawFile(qar, disk); Interlocked.Increment(ref rawCount); }
                cache.Set(disk, fingerprint);
            }

            var d = Interlocked.Increment(ref done);
            ApplyProgressed?.Invoke(totalHosts == 0 ? 1.0 : (double)d / totalHosts);
        });

        Log("");
        Log($"fpk hosts rebuilt: {fpkCount}, raw files copied: {rawCount}, skipped (unchanged): {skipped}");

        foreach (var mod in State.Mods)
        {
            if (!mod.Enabled || mod.GameDirEntries.Count == 0) continue;
            Log("");
            Log($"== gamedir overlay for {mod.Id}");
            ApplyGameDir(mod);
        }

        // Orphan cleanup: paths the cache says we wrote to in a previous Apply
        // but that no currently-enabled mod ships any more. Restore from
        // baseline when one exists, delete the on-disk file otherwise (the
        // .absent marker means the file was mod-introduced and shouldn't
        // outlive its mod). Removes the orphan from the cache so future
        // Applies don't re-check it.
        var currentDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var qar in hosts) currentDisk.Add(Paths.ResolveQar(qar, State.GameRoot));

        int restored = 0, removed = 0;
        foreach (var orphan in cache.KnownDiskPaths)
        {
            if (currentDisk.Contains(orphan)) continue;
            var baseline = BaselineFor(orphan);
            var marker   = baseline + ".absent";
            try
            {
                if (File.Exists(baseline))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
                    File.Copy(baseline, orphan, overwrite: true);
                    restored++;
                }
                else if (File.Exists(marker))
                {
                    if (File.Exists(orphan)) { File.Delete(orphan); removed++; }
                }
            }
            catch (Exception ex) { Log($"  WARN cleaning orphan {orphan}: {ex.Message}"); }
            cache.Invalidate(orphan);
        }
        if (restored + removed > 0)
            Log($"Orphan cleanup: restored {restored} vanilla file(s), removed {removed} mod-introduced file(s).");

        // Sweep tmp/host_* dirs that aren't backing a current host.
        var currentHostDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var qar in hosts)
        {
            var disk = Paths.ResolveQar(qar, State.GameRoot);
            currentHostDirs.Add("host_" + SafeHostKey(qar, Path.GetFileName(disk)));
        }
        if (Directory.Exists(TmpDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(TmpDir, "host_*"))
            {
                if (currentHostDirs.Contains(Path.GetFileName(dir))) continue;
                try { Directory.Delete(dir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        cache.Save();

        // Mark each mod's Applied state to reflect what's now in the install.
        // Enabled mods that contributed are "applied"; disabled mods are not.
        foreach (var m in State.Mods) m.Applied = m.Enabled;
        SaveState();

        Log("");
        Log($"Apply complete. Temp tree left at {TmpDir}");
    }

    /// <summary>
    /// Wipe the Apply-cache and per-host scratch dirs so the next Apply
    /// rebuilds every host from scratch. Doesn't touch the game install
    /// (use Revert for that), the workspace state (use Remove for that),
    /// or the dictionary files (use the dictionary export action).
    /// Returns the number of artifacts cleared.
    /// </summary>
    /// <summary>
    /// Walks every enabled mod's contributions (QAR paths, loose
    /// gamedir entries, FPK inner entries) in load-order priority and
    /// returns the file paths that more than one mod wants to provide.
    /// For each conflict, <see cref="ConflictInfo.Contributors"/> lists
    /// the mod ids with the load-order winner first.
    /// </summary>
    public List<ConflictInfo> DetectConflicts()
    {
        // Three separate buckets so we can label each conflict by
        // layer (a "QAR vs QAR" collision means something different
        // visually to the user than "FPK vs FPK" — they cohabit but
        // are managed differently at Apply time).
        var qar     = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var gamedir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var fpk     = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in State.Mods)
        {
            if (!mod.Enabled) continue;
            foreach (var p in mod.QarPaths)
                AddTo(qar, p, mod.Id);
            foreach (var g in mod.GameDirEntries)
                AddTo(gamedir, g, mod.Id);
            foreach (var (host, entries) in mod.FpkEntries)
                foreach (var e in entries)
                    AddTo(fpk, $"{host} :: {e}", mod.Id);
        }

        var conflicts = new List<ConflictInfo>();
        Collect(qar,     "QAR",     conflicts);
        Collect(gamedir, "GAMEDIR", conflicts);
        Collect(fpk,     "FPK",     conflicts);
        return conflicts
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy (c => c.Path,     StringComparer.Ordinal)
            .ToList();

        static void AddTo(Dictionary<string, List<string>> map, string path, string modId)
        {
            if (!map.TryGetValue(path, out var list)) map[path] = list = new();
            list.Add(modId);
        }

        static void Collect(Dictionary<string, List<string>> map, string category, List<ConflictInfo> sink)
        {
            foreach (var (path, mods) in map)
            {
                if (mods.Count < 2) continue;
                sink.Add(new ConflictInfo
                {
                    Path         = path,
                    Category     = category,
                    Contributors = mods,
                });
            }
        }
    }

    public (int cacheEntriesCleared, int hostDirsCleared) ResetApplyState()
    {
        EnsureInitialised();

        var cachePath = Path.Combine(WorkspaceDir, "apply-cache.txt");
        var cacheClears = 0;
        if (File.Exists(cachePath))
        {
            try { cacheClears = File.ReadAllLines(cachePath).Count(l => l.Contains('=') && !l.StartsWith('#')); }
            catch { /* best effort */ }
            File.Delete(cachePath);
        }

        var dirClears = 0;
        if (Directory.Exists(TmpDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(TmpDir, "host_*"))
            {
                try { Directory.Delete(dir, recursive: true); dirClears++; } catch { /* best effort */ }
            }
        }

        Log($"Reset Apply state: cleared {cacheClears} cache entr(ies) and {dirClears} host scratch dir(s).");
        return (cacheClears, dirClears);
    }

    /// <summary>Restore every original game file and delete every mod-introduced file.</summary>
    public void RevertAll()
    {
        EnsureInitialised();
        if (!Directory.Exists(BaselineDir))
        {
            Log("No original-file cache; nothing to revert.");
            return;
        }

        int restored = 0, deleted = 0;
        foreach (var file in Directory.EnumerateFiles(BaselineDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(BaselineDir, file);
            if (Path.GetExtension(rel) == ".absent")
            {
                var origRel = Path.ChangeExtension(rel, null)!;
                var disk    = Path.Combine(State.GameRoot, origRel);
                if (File.Exists(disk)) { File.Delete(disk); deleted++; }
            }
            else
            {
                var disk = Path.Combine(State.GameRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                File.Copy(file, disk, overwrite: true);
                restored++;
            }
        }
        // Nothing from any mod is in the install any more — wipe the cache
        // so the next Apply rebuilds every host from scratch.
        var cachePath = Path.Combine(WorkspaceDir, "apply-cache.txt");
        if (File.Exists(cachePath)) File.Delete(cachePath);

        foreach (var m in State.Mods) m.Applied = false;
        SaveState();

        Log($"Reverted {restored} file(s), removed {deleted} mod-added file(s).");
    }

    // ─── Internals: state guards ───────────────────────────────────────────

    private void EnsureInitialised()
    {
        if (string.IsNullOrEmpty(State.GameRoot))
            throw new InvalidOperationException("Not initialised. Set game_root and datfpk first.");
        if (!Directory.Exists(State.GameRoot))
            throw new InvalidOperationException("game_root does not exist: " + State.GameRoot);
    }

    private ModInfo FindMod(string id)
        => State.Mods.FirstOrDefault(m => m.Id == id)
           ?? throw new InvalidOperationException("No such mod id: " + id);

    // ─── Internals: filesystem helpers ─────────────────────────────────────

    private static void ExtractZip(string zip, string outDir)
    {
        Directory.CreateDirectory(outDir);
        ZipFile.ExtractToDirectory(zip, outDir, overwriteFiles: true);
    }

    private static void ScanGameDir(string modRoot, List<string> sink)
    {
        if (!Directory.Exists(modRoot)) return;
        const string marker = "gamedir/";
        foreach (var file in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories))
        {
            var rel   = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
            var lower = rel.ToLowerInvariant();
            var idx   = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = rel.Substring(idx + marker.Length);
            if (rest.Length > 0) sink.Add(rest);
        }
    }

    private static void DeduplicateInPlace(List<string> list)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        list.RemoveAll(x => !seen.Add(x));
    }

    private string BaselineFor(string diskPath)
        => Path.Combine(BaselineDir, Path.GetRelativePath(State.GameRoot, diskPath));

    /// <summary>
    /// Snapshot the disk file before any mod touches it. Files that did not
    /// exist pre-mod get a sentinel <c>.absent</c> marker so revert removes
    /// them rather than restoring stale data.
    /// </summary>
    private void EnsureBaseline(string diskPath)
    {
        var bak    = BaselineFor(diskPath);
        var marker = bak + ".absent";
        if (File.Exists(bak) || File.Exists(marker)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
        if (File.Exists(diskPath)) File.Copy(diskPath, bak);
        else                       File.WriteAllText(marker, "absent\n");
    }

    private int RunChildProcess(string exe, string args)
    {
        Log($"$ \"{exe}\" {args}");
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Log(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    // ─── Internals: datfpk wrappers ────────────────────────────────────────

    private string DatfpkUnpack(string fpkPath, string workDir)
    {
        Directory.CreateDirectory(workDir);
        var local = Path.Combine(workDir, Path.GetFileName(fpkPath));
        File.Copy(fpkPath, local, overwrite: true);
        if (RunChildProcess(State.DatFpk, $"\"{local}\"") != 0)
            throw new Exception("datfpk unpack failed: " + local);
        return local;
    }

    private void DatfpkPack(string jsonPath, string outPath, string inputDir)
    {
        if (RunChildProcess(State.DatFpk, $"\"{jsonPath}\" \"{outPath}\" \"{inputDir}\"") != 0)
            throw new Exception("datfpk pack failed: " + jsonPath);
    }

    // ─── Internals: per-host rebuild ───────────────────────────────────────

    private string? FindFirstEnabledPayload(string qarPath, out ModInfo? winner)
    {
        winner = null;
        foreach (var mod in State.Mods)
        {
            if (!mod.Enabled || !mod.QarPaths.Contains(qarPath)) continue;
            var payload = ModPayloadFor(mod, qarPath);
            if (!File.Exists(payload)) continue;
            winner = mod;
            return payload;
        }
        return null;
    }

    private string ModPayloadFor(ModInfo mod, string qarPath)
    {
        var inner = qarPath.StartsWith("/") ? qarPath[1..] : qarPath;
        var modRoot = ModUnpackDir(mod.Id);
        if (!Directory.Exists(modRoot)) ExtractZip(mod.Source, modRoot);
        return Path.Combine(modRoot, inner.Replace('/', Path.DirectorySeparatorChar));
    }

    private void RebuildRawFile(string qarPath, string diskPath)
    {
        Log("");
        Log("== copy " + qarPath);
        Log("   disk: " + diskPath);

        var src = FindFirstEnabledPayload(qarPath, out var winner);
        if (src is null || winner is null)
        {
            Log("   no enabled mod ships this path; restoring original");
            return;
        }

        EnsureBaseline(diskPath);
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        File.Copy(src, diskPath, overwrite: true);
        Log("   from mod: " + winner.Id);
        Log("   -> wrote " + diskPath);
    }

    private void RebuildFpkHost(string qarPath, string diskPath)
    {
        Log("");
        Log("== rebuild fpk " + qarPath);
        Log("   disk: " + diskPath);

        // Mods ship PARTIAL fpks containing only their edits; we must merge
        // them onto the vanilla baseline. The one exception is mod-introduced
        // files (no vanilla baseline at all): there's no merge to do because
        // the entire pack belongs to the mod. In that case the mod's fpk IS
        // the final file and we copy through to avoid the lossy datfpk
        // unpack/repack round-trip (which drops hash-only Path fields inside
        // vehicle bodies, etc.).
        EnsureBaseline(diskPath);
        var baseline       = BaselineFor(diskPath);
        var baselineExists = File.Exists(baseline);

        if (!baselineExists)
        {
            var loneContrib = (ModInfo?)null;
            var contribCount = 0;
            foreach (var m in State.Mods)
            {
                if (!m.Enabled || !m.QarPaths.Contains(qarPath)) continue;
                contribCount++;
                loneContrib ??= m;
            }

            if (contribCount == 1 && loneContrib is not null)
            {
                var payload = ModPayloadFor(loneContrib, qarPath);
                if (!File.Exists(payload))
                {
                    Log("   WARN: single contributor's payload missing: " + loneContrib.Id);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                File.Copy(payload, diskPath, overwrite: true);
                Log($"   passthrough from {loneContrib.Id} (mod-introduced, no merge needed)");
                Log($"   -> wrote {diskPath}");
                return;
            }
        }

        // Work-dir name must be unique per qar path, not per filename — two
        // distinct qar paths can share a filename (e.g. .../EngText/mgo_player_subtitles.fpkd
        // and .../FreText/mgo_player_subtitles.fpkd). Aliasing them onto the
        // same scratch dir is unsafe under parallel Apply.
        var work = Path.Combine(TmpDir, "host_" + SafeHostKey(qarPath, Path.GetFileName(diskPath)));
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var diskExt           = Path.GetExtension(diskPath);
        var refsUnion         = new List<string>();
        var type              = "fpkd";
        string extractedRoot;

        if (baselineExists)
        {
            var local = DatfpkUnpack(baseline, work);
            var json  = local + ".json";
            if (!File.Exists(json)) throw new Exception("expected json not found: " + json);
            var text = FpkJson.Read(json);
            type     = FpkJson.ReadType(text);
            refsUnion.AddRange(FpkJson.ReadReferences(text));
            extractedRoot = Path.Combine(work, Path.GetFileNameWithoutExtension(local) + "_" + Path.GetExtension(local)[1..]);
        }
        else
        {
            var firstPayload = FindFirstEnabledPayload(qarPath, out var first);
            if (firstPayload is null || first is null)
            {
                Log("   no enabled contributor ships a payload; skipping");
                return;
            }
            Log("   bootstrapping from " + first.Id);
            var local = DatfpkUnpack(firstPayload, work);
            var json  = local + ".json";
            if (File.Exists(json))
            {
                var text = FpkJson.Read(json);
                type     = FpkJson.ReadType(text);
                refsUnion.AddRange(FpkJson.ReadReferences(text));
            }
            extractedRoot = Path.Combine(work, Path.GetFileNameWithoutExtension(local) + "_" + Path.GetExtension(local)[1..]);
        }
        if (!Directory.Exists(extractedRoot)) Directory.CreateDirectory(extractedRoot);

        // Overlay in reverse list order so the TOP mod is the last to write,
        // and therefore the one whose files survive any conflict. Priority
        // rule throughout the manager: higher in the list = higher priority.
        for (var i = State.Mods.Count - 1; i >= 0; i--)
        {
            var mod = State.Mods[i];
            if (!mod.Enabled || !mod.QarPaths.Contains(qarPath)) continue;
            var payload = ModPayloadFor(mod, qarPath);
            if (!File.Exists(payload))
            {
                Log("   WARN: mod missing payload: " + mod.Id);
                continue;
            }
            Log("   overlay: " + mod.Id);

            var modWork  = Path.Combine(work, "from_" + mod.Id);
            Directory.CreateDirectory(modWork);
            var modLocal = DatfpkUnpack(payload, modWork);
            var modExt   = Path.Combine(modWork, Path.GetFileNameWithoutExtension(modLocal) + "_" + Path.GetExtension(modLocal)[1..]);
            if (Directory.Exists(modExt)) CopyTreeOverlay(modExt, extractedRoot);

            var modJson = modLocal + ".json";
            if (File.Exists(modJson))
                refsUnion.AddRange(FpkJson.ReadReferences(FpkJson.Read(modJson)));
        }

        var refs = refsUnion.Distinct(StringComparer.Ordinal)
                            .OrderBy(x => x, StringComparer.Ordinal)
                            .ToList();
        var entries  = WalkAsQarEntries(extractedRoot);
        entries      = SortEntriesForArchive(entries, type);
        var manifest = FpkJson.Write(type, entries, refs);

        var mergedJson = Path.Combine(work, "merged" + diskExt + ".json");
        File.WriteAllText(mergedJson, manifest);

        var outTmp = Path.Combine(work, "out" + diskExt);
        DatfpkPack(mergedJson, outTmp, extractedRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        File.Copy(outTmp, diskPath, overwrite: true);
        Log($"   -> wrote {diskPath}  (entries={entries.Count}, refs={refs.Count})");
    }

    private static void CopyTreeOverlay(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel    = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Build a filesystem-safe work-dir key that disambiguates qar paths with
    /// the same filename. Format: <c>{filename}_{8hex}</c>, where the hex is
    /// derived from the full qar path. Stable across runs.
    /// </summary>
    private static string SafeHostKey(string qarPath, string fileName)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(qarPath));
        var hex = Convert.ToHexStringLower(bytes, 0, 4);
        return fileName + "_" + hex;
    }

    private static List<string> WalkAsQarEntries(string root)
    {
        var entries = new List<string>();
        if (!Directory.Exists(root)) return entries;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Length > 0) entries.Add("/" + rel);
        }
        return entries;
    }

    // ─── Fpk entry ordering ────────────────────────────────────────────────
    //
    // Vanilla TPP fpk(d) archives have a strict, non-obvious entry order that
    // the game's loader depends on. Ports of SnakeBite's table; with these,
    // SnakeBite repacks every vanilla fpk byte-identically. Plain alphabetical
    // ordering is *almost* right for .fpk but wrong for .fpkd (alpha is
    // descending there), and both formats additionally regroup entries by a
    // hardcoded extension priority — get this wrong and the game reads e.g.
    // a vehicle's .veh blob in the wrong slot and crashes in BodyBase init.
    //
    // Source: SnakeBite/Classes/GzsLib.cs (SortFpksFiles, archiveExtensions).

    private static readonly string[] FpkExtensionOrder = new[]
    {
        "caar", "fnt", "atsh", "frig", "adm", "frt", "fpkl", "fsm", "ftdp",
        "geobv", "ftex", "geoms", "gimr", "gpfp", "grxla", "grxoc", "htre",
        "lba", "lpsh", "mog", "mtar", "nav2", "nta", "rdf", "ends", "sand",
        "mbl", "tcvp", "spch", "trap", "uigb", "uilb", "pcsp", "tre2", "fstb",
        "twpf", "fv2t", "fmdl", "geom", "gskl", "fcnp", "frdv", "fdes", "fclo",
        "uif", "uia", "subp", "sani", "ladb", "frl", "fv2", "obr", "lng2",
        "mtard", "obrb", "dfrm",
    };

    private static readonly string[] FpkdExtensionOrder = new[]
    {
        "fox2", "evf", "parts", "vfxlb", "vfx", "vfxlf", "veh", "frld",
        "des", "bnd", "tgt", "phsd", "ph", "sim", "clo", "fsd", "sdf",
        "lua", "lng",
    };

    /// <summary>
    /// Sort entries into the order an fpk/fpkd's loader expects.
    /// <paramref name="archiveType"/> is the literal "fpk" or "fpkd".
    /// </summary>
    private static List<string> SortEntriesForArchive(List<string> entries, string archiveType)
    {
        if (entries.Count <= 1) return entries;

        var isFpkd = string.Equals(archiveType, "fpkd", StringComparison.OrdinalIgnoreCase);
        var working = new List<string>(entries);

        // Pass 1: alphabetical. fpk = ascending, fpkd = descending.
        if (isFpkd) working.Sort((a, b) => string.CompareOrdinal(b, a));
        else        working.Sort(StringComparer.Ordinal);

        // Pass 2: regroup by extension priority. Extensions not in the table
        // fall through to a stable trailing group preserving the pass-1 order.
        var order = isFpkd ? FpkdExtensionOrder : FpkExtensionOrder;
        var sorted = new List<string>(working.Count);
        foreach (var ext in order)
        {
            for (var i = 0; i < working.Count; i++)
            {
                var path = working[i];
                if (path is null) continue;
                var fileExt = Path.GetExtension(path);
                if (fileExt.Length > 1 && string.Equals(fileExt[1..], ext, StringComparison.OrdinalIgnoreCase))
                {
                    sorted.Add(path);
                    working[i] = null!;   // mark consumed
                }
            }
        }
        foreach (var leftover in working)
            if (leftover is not null) sorted.Add(leftover);

        return sorted;
    }

    private void ApplyGameDir(ModInfo mod)
    {
        Log($"   gamedir files: {mod.GameDirEntries.Count}");
        var modRoot = ModUnpackDir(mod.Id);
        if (!Directory.Exists(modRoot)) ExtractZip(mod.Source, modRoot);

        foreach (var rel in mod.GameDirEntries)
        {
            var src = Path.Combine(modRoot, "GameDir", rel);
            if (!File.Exists(src)) src = Path.Combine(modRoot, rel);
            if (!File.Exists(src))
            {
                // Case-insensitive search for any "*/GameDir/<rel>" anywhere in the tree.
                var suffix = ("/gamedir/" + rel).ToLowerInvariant();
                foreach (var candidate in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories))
                {
                    if (candidate.Replace('\\', '/').ToLowerInvariant().EndsWith(suffix))
                    {
                        src = candidate;
                        break;
                    }
                }
            }
            if (!File.Exists(src))
            {
                Log("   WARN: gamedir source missing for " + rel);
                continue;
            }

            var dst = Path.Combine(State.GameRoot, rel);
            EnsureBaseline(dst);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            Log("   gamedir -> " + dst);
        }
    }
}
