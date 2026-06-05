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
public sealed partial class ModManager
{
    public ModManager(string? workspaceDir = null)
    {
        WorkspaceDir = workspaceDir ?? Path.GetDirectoryName(Environment.ProcessPath ?? ".") ?? ".";
        // Shared XML config — same file modbldr reads/writes. See
        // StateIo for the schema. First-launch migration picks up
        // the legacy state.txt next to the exe if present.
        StatePath    = StateIo.DefaultPath();
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

        // Insert at the top of the load order rather than appending.
        // Top-wins priority is the established rule (see the row-
        // selection / conflict-resolution logic), so a freshly-added
        // mod becoming the highest-priority entry matches what the
        // user almost always wants: "I just installed this, make it
        // the one that's active." Persistence reads/writes State.Mods
        // in order so this carries through to state.txt automatically,
        // and SyncRows mirrors the collection 1:1 so the DataGrid
        // rows reflect the new order on the next view refresh.
        State.Mods.Insert(0, mod);
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

}