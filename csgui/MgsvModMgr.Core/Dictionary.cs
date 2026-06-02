namespace MgsvModMgr.Core;

// PathDictionary.txt + ExplicitPathDictionary.txt maintenance.
// dict_base: strip at the FIRST '.' of the final filename segment, mirroring
// the boundary PathCode uses for its base-hash. Multi-dot names like
// "ih_general.fre.lng2" collapse to "ih_general" so one entry suffices.
public static class DictionaryWriter
{
    public static string PathDictFile(string gameRoot)
        => Path.Combine(gameRoot, "PathDictionary.txt");

    public static string ExplicitDictFile(string gameRoot)
        => Path.Combine(gameRoot, "ExplicitPathDictionary.txt");

    /// <summary>
    /// Ensure the two dictionary files exist next to the game exe. If a
    /// target is missing or zero-byte, copy the baseline file shipped
    /// alongside the modmgr exe (workspaceDir). Existing non-empty files
    /// are left alone so the user's prior customisations are not clobbered.
    /// </summary>
    public static void EnsureBaselinesAt(string gameRoot, string workspaceDir, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return;
        SeedOne(gameRoot, workspaceDir, "PathDictionary.txt",        log);
        SeedOne(gameRoot, workspaceDir, "ExplicitPathDictionary.txt", log);
    }

    private static void SeedOne(string gameRoot, string workspaceDir, string fileName, Action<string>? log)
    {
        var target = Path.Combine(gameRoot, fileName);
        if (File.Exists(target) && new FileInfo(target).Length > 0) return;

        var source = Path.Combine(workspaceDir, fileName);
        if (!File.Exists(source))
        {
            log?.Invoke(
                $"WARNING: baseline {fileName} not found beside modmgr ({source}). " +
                "Apply will still run but the game will be missing vanilla path codes; " +
                "drop the FoxKit copy into the modmgr folder and re-Init.");
            return;
        }

        try
        {
            File.Copy(source, target, overwrite: false);
            log?.Invoke($"Seeded {fileName} -> {target} ({new FileInfo(target).Length / 1024} KB).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"WARNING: could not seed {fileName} to {gameRoot}: {ex.Message}");
        }
    }

    public static string DictBase(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        p = p.Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        int nameStart = slash < 0 ? 0 : slash + 1;
        int dot = p.IndexOf('.', nameStart);
        if (dot >= 0) p = p[..dot];
        return p;
    }

    public static int UpdateFromMod(ModInfo m, string gameRoot, string? workspaceDir = null, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return 0;

        // Self-heal: if the dicts went missing between Applies, re-seed them
        // from the baselines beside the modmgr exe before we touch either file.
        if (!string.IsNullOrEmpty(workspaceDir))
            EnsureBaselinesAt(gameRoot, workspaceDir, log);

        var pd = PathDictFile(gameRoot);
        var ed = ExplicitDictFile(gameRoot);

        var havePaths = ReadLinesSet(pd);
        var addPaths  = new List<string>();
        void Consider(string raw)
        {
            var b = DictBase(raw);
            if (string.IsNullOrEmpty(b)) return;
            if (havePaths.Add(b)) addPaths.Add(b);
        }
        foreach (var q in m.QarPaths) Consider(q);
        foreach (var kv in m.FpkEntries)
        {
            Consider(kv.Key);
            foreach (var i in kv.Value) Consider(i);
        }
        if (addPaths.Count > 0) File.AppendAllLines(pd, addPaths);

        var haveHex = ReadExplicitHex(ed);
        var addExpl = new List<string>();
        foreach (var kv in m.QarHashes)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (!ulong.TryParse(kv.Value, out var h)) continue;
            var hex = $"0x{h:x16}";
            if (haveHex.Add(hex)) addExpl.Add($"{hex}\t{kv.Key}");
        }
        if (addExpl.Count > 0) File.AppendAllLines(ed, addExpl);

        return addPaths.Count + addExpl.Count;
    }

    private static HashSet<string> ReadLinesSet(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return set;
        foreach (var l in File.ReadLines(path))
            if (l.Length > 0) set.Add(l);
        return set;
    }

    private static HashSet<string> ReadExplicitHex(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return set;
        foreach (var l in File.ReadLines(path))
        {
            int tab = l.IndexOf('\t');
            if (tab > 0) set.Add(l[..tab].ToLowerInvariant());
        }
        return set;
    }
}
