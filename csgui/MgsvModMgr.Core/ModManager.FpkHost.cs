using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MgsvModMgr.Core;

// ModManager — per-fpk-host rebuild internals + SnakeBite-compatible
// FPK entry ordering tables. The single biggest chunk of the manager
// — extracted so the main file stays readable.
public sealed partial class ModManager
{
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