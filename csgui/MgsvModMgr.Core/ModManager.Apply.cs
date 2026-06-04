using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MgsvModMgr.Core;

// ModManager — Apply / Revert / ResetApplyState / DetectConflicts.
// The whole game-install mutation pipeline lives here so the main
// ModManager.cs stays focused on lifecycle + mod CRUD.
public sealed partial class ModManager
{
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
}