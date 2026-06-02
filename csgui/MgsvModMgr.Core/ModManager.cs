using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MgsvModMgr.Core;

/// <summary>
/// Top-level orchestrator: owns the persisted <see cref="State"/>, handles
/// add/enable/remove/move/apply/revert, and keeps the two PathDictionary
/// files next to the game exe in sync with whatever the installed mods ship.
/// </summary>
public sealed class ModManager
{
    public ModManager(string? rootDir = null)
    {
        RootDir    = rootDir ?? Path.GetDirectoryName(Environment.ProcessPath ?? ".") ?? ".";
        StatePath  = Path.Combine(RootDir, "state.txt");
        BackupsDir = Path.Combine(RootDir, "backups");
        ModsDir    = Path.Combine(RootDir, "mods");
        TmpDir     = Path.Combine(RootDir, "tmp");
    }

    /// <summary>Directory the manager writes state/backups/tmp under (defaults to the running exe's folder).</summary>
    public string RootDir    { get; }
    public string StatePath  { get; }
    public string BackupsDir { get; }
    public string ModsDir    { get; }
    public string TmpDir     { get; }

    public State State { get; } = new();

    /// <summary>Sink for human-readable progress lines.</summary>
    public Action<string> Log { get; set; } = _ => { };

    // ─── State I/O ─────────────────────────────────────────────────────────

    public void LoadState() => StateIo.Load(State, StatePath);
    public void SaveState() => StateIo.Save(State, StatePath);

    /// <summary>Set the MGSV install root + datfpk path, then persist.</summary>
    public void Init(string gameRoot, string datfpk)
    {
        State.GameRoot = Path.GetFullPath(gameRoot);
        State.DatFpk   = Path.GetFullPath(datfpk);
        SaveState();
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

        var dictAdded = DictionaryWriter.UpdateFromMod(mod, State.GameRoot);
        Log($"Added: {mod.Id}  ({mod.Name} v{mod.Version})");
        Log($"  qar entries:     {mod.QarPaths.Count}");
        Log($"  fpk hosts:       {mod.FpkEntries.Count}");
        Log($"  gamedir entries: {mod.GameDirEntries.Count}");
        Log($"  dictionary +{dictAdded} new entries");
    }

    public void EnableMod(string id, bool enabled)
    {
        FindMod(id).Enabled = enabled;
        SaveState();
    }

    public void RemoveMod(string id)
    {
        var mod = FindMod(id);
        if (File.Exists(mod.Source)) File.Delete(mod.Source);
        State.Mods.Remove(mod);
        SaveState();
    }

    public void MoveMod(string id, int delta)
    {
        var index = -1;
        for (var i = 0; i < State.Mods.Count; i++)
            if (State.Mods[i].Id == id) { index = i; break; }
        if (index < 0) throw new InvalidOperationException("No such mod id: " + id);

        var target = index + delta;
        if (target < 0 || target >= State.Mods.Count) return;
        State.Mods.Move(index, target);
        SaveState();
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
            total += DictionaryWriter.UpdateFromMod(mod, State.GameRoot);
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

        var hosts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var mod in State.Mods)
            foreach (var qar in mod.QarPaths) hosts.Add(qar);

        int fpkCount = 0, rawCount = 0;
        foreach (var qar in hosts)
        {
            var disk = Paths.ResolveQar(qar, State.GameRoot);
            if (Paths.QarIsFpk(qar)) { RebuildFpkHost(qar, disk); fpkCount++; }
            else                      { RebuildRawFile(qar, disk); rawCount++; }
        }

        Log("");
        Log($"fpk hosts: {fpkCount}, raw files: {rawCount}");

        foreach (var mod in State.Mods)
        {
            if (!mod.Enabled || mod.GameDirEntries.Count == 0) continue;
            Log("");
            Log($"== gamedir overlay for {mod.Id}");
            ApplyGameDir(mod);
        }

        Log("");
        Log($"Apply complete. Temp tree left at {TmpDir}");
    }

    /// <summary>Restore every backed-up file and delete every mod-introduced file.</summary>
    public void RevertAll()
    {
        EnsureInitialised();
        if (!Directory.Exists(BackupsDir))
        {
            Log("No backups; nothing to revert.");
            return;
        }

        int restored = 0, deleted = 0;
        foreach (var file in Directory.EnumerateFiles(BackupsDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(BackupsDir, file);
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

    private string BackupFor(string diskPath)
        => Path.Combine(BackupsDir, Path.GetRelativePath(State.GameRoot, diskPath));

    /// <summary>
    /// Snapshot the disk file before any mod touches it. Files that did not
    /// exist pre-mod get a sentinel <c>.absent</c> marker so revert removes
    /// them rather than restoring stale data.
    /// </summary>
    private void EnsureBackup(string diskPath)
    {
        var bak    = BackupFor(diskPath);
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
            Log("   no enabled mod ships this path; reverting from backup");
            return;
        }

        EnsureBackup(diskPath);
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

        var work = Path.Combine(TmpDir, "host_" + Path.GetFileName(diskPath));
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        EnsureBackup(diskPath);
        var backup            = BackupFor(diskPath);
        var baselineExists    = File.Exists(backup);
        var diskExt           = Path.GetExtension(diskPath);
        var refsUnion         = new List<string>();
        var type              = "fpkd";
        string extractedRoot;

        if (baselineExists)
        {
            var local = DatfpkUnpack(backup, work);
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

        foreach (var mod in State.Mods)
        {
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
        var entries = WalkAsQarEntries(extractedRoot);
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

    private static List<string> WalkAsQarEntries(string root)
    {
        var entries = new List<string>();
        if (!Directory.Exists(root)) return entries;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Length > 0) entries.Add("/" + rel);
        }
        entries.Sort(StringComparer.Ordinal);
        return entries;
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
            EnsureBackup(dst);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            Log("   gamedir -> " + dst);
        }
    }
}
