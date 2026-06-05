using System.Collections.ObjectModel;
using System.Xml.Linq;

namespace MgsvModMgr.Core;

/// <summary>
/// UI theme preference. <see cref="Auto"/> follows the operating
/// system's current theme — at runtime Avalonia maps that to
/// <c>ThemeVariant.Default</c>, which honours the OS setting and
/// updates live when the user toggles light/dark in their OS.
/// </summary>
public enum ThemeMode
{
    Dark = 0,
    Light = 1,
    Auto = 2,
}

public sealed class State
{
    public string GameRoot { get; set; } = "";
    public string DatFpk   { get; set; } = "";
    /// <summary>Personal API key from nexusmods.com/users/myaccount?tab=api.</summary>
    public string NexusApiKey { get; set; } = "";
    /// <summary>
    /// UI theme preference. Persisted under <c>&lt;shared&gt;&lt;theme&gt;</c>.
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    /// <summary>
    /// UI language code (matches the <c>xx</c> in
    /// <c>Lang/Strings.xx.axaml</c>). Defaults to <c>en</c>; the
    /// startup locale loader falls back to English if the saved code
    /// no longer has a strings file.
    /// </summary>
    public string Language { get; set; } = "en";
    public ObservableCollection<ModInfo> Mods { get; } = new();

    /// <summary>
    /// UI preference: column headers (TAGS / VERSION / AUTHOR / QAR /
    /// GAMEDIR) the user has hidden via the header context menu.
    /// </summary>
    public HashSet<string> HiddenColumns { get; } = new();
}

/// <summary>
/// Read/write modmgr's <c>manager.xml</c>, kept next to the running
/// exe in its workspace folder. The file is portable: copy the exe +
/// its <c>manager.xml</c> / <c>mods/</c> / <c>root/</c> folders
/// together and modmgr works the same on the destination machine.
/// No <c>%APPDATA%</c>, no hidden state.
///
/// Schema:
/// <code>
/// &lt;mgsv_tools&gt;
///   &lt;shared&gt;     datfpk, theme, language     &lt;/shared&gt;
///   &lt;modmgr&gt;     game_root, mods, ...        &lt;/modmgr&gt;
/// &lt;/mgsv_tools&gt;
/// </code>
///
/// The <c>&lt;shared&gt;</c> subtree is just internal organisation —
/// fields that conceptually belong to "the user's mgsv-tools setup"
/// rather than "modmgr's mod list". No other tool reads this file.
///
/// One-time migration: legacy <c>state.txt</c> from the old key-value
/// format (same directory as manager.xml) is parsed on first launch
/// and rewritten as XML. The old file is left in place as a backup,
/// never deleted.
/// </summary>
public static class StateIo
{
    public const string RootEl    = "mgsv_tools";
    public const string SharedEl  = "shared";
    public const string ModMgrEl  = "modmgr";

    /// <summary>
    /// Location of <c>manager.xml</c> — next to the calling exe.
    /// Falls back to the current working directory when
    /// <see cref="Environment.ProcessPath"/> is unavailable (rare,
    /// mostly under exotic launchers).
    /// </summary>
    public static string DefaultPath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
                  ?? Directory.GetCurrentDirectory();
        return Path.Combine(dir, "manager.xml");
    }


    public static void Load(State s, string xmlPath)
    {
        // Reset to defaults.
        s.GameRoot = ""; s.DatFpk = ""; s.NexusApiKey = "";
        s.Theme = ThemeMode.Dark; s.Language = "en";
        s.Mods.Clear(); s.HiddenColumns.Clear();

        if (!File.Exists(xmlPath))
        {
            // First-launch migration from the legacy state.txt
            // key-value format. Both files live in the same dir
            // (next to the exe), so the probe is just "state.txt
            // alongside the missing manager.xml". If found, load it
            // and rewrite as XML — the legacy file stays in place as
            // a backup.
            var legacy = Path.Combine(
                Path.GetDirectoryName(xmlPath) ?? ".",
                "state.txt");
            if (File.Exists(legacy))
            {
                LoadLegacyKv(s, legacy);
                try { Save(s, xmlPath); } catch { /* best effort */ }
            }
            return;
        }

        try
        {
            var doc = XDocument.Load(xmlPath);
            var root = doc.Element(RootEl);
            if (root is null) return;

            var shared = root.Element(SharedEl);
            if (shared is not null)
            {
                s.DatFpk   = shared.Element("datfpk")?.Value   ?? "";
                s.Theme    = ParseTheme(shared.Element("theme")?.Value ?? "");
                var lang   = shared.Element("language")?.Value ?? "";
                if (lang.Length > 0) s.Language = lang;
            }

            var mm = root.Element(ModMgrEl);
            if (mm is not null)
            {
                s.GameRoot    = mm.Element("game_root")?.Value    ?? "";
                // Stored as an encrypted blob (see SecretVault). Decrypt
                // at the storage boundary so callers see the plaintext key.
                s.NexusApiKey = SecretVault.Decrypt(mm.Element("nexus_apikey")?.Value);

                var hidden = mm.Element("hidden_columns");
                if (hidden is not null)
                    foreach (var c in hidden.Elements("column"))
                        if (!string.IsNullOrEmpty(c.Value)) s.HiddenColumns.Add(c.Value);

                var mods = mm.Element("mods");
                if (mods is not null)
                    foreach (var modEl in mods.Elements("mod"))
                        s.Mods.Add(ReadMod(modEl));
            }
        }
        catch
        {
            // Malformed XML: fall back to defaults. UI will rewrite
            // on next change.
        }
    }

    public static void Save(State s, string xmlPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);

        // Modmgr owns this file outright; we always rewrite from
        // current State (no preservation of foreign subtrees — sibling
        // tools live in their own files in mgsv_tools/).
        var doc  = new XDocument(new XElement(RootEl));
        var root = doc.Root!;

        root.Add(new XElement(SharedEl,
            new XElement("datfpk",   s.DatFpk),
            new XElement("theme",    ThemeString(s.Theme)),
            new XElement("language", s.Language)));

        var mmEl = new XElement(ModMgrEl,
            new XElement("game_root",    s.GameRoot),
            // Encrypt before the key leaves memory. On Windows the
            // blob is DPAPI-encrypted against the current user so a
            // stolen manager.xml decrypts only on the originating
            // machine + account.
            new XElement("nexus_apikey", SecretVault.Encrypt(s.NexusApiKey)));

        if (s.HiddenColumns.Count > 0)
        {
            var hidden = new XElement("hidden_columns");
            foreach (var c in s.HiddenColumns) hidden.Add(new XElement("column", c));
            mmEl.Add(hidden);
        }

        var mods = new XElement("mods");
        foreach (var m in s.Mods) mods.Add(WriteMod(m));
        mmEl.Add(mods);

        root.Add(mmEl);
        doc.Save(xmlPath);
    }

    // ─── ModInfo XML mapping ─────────────────────────────────────────────

    private static ModInfo ReadMod(XElement el)
    {
        var m = new ModInfo
        {
            Id      = el.Element("id")?.Value      ?? "",
            Name    = el.Element("name")?.Value    ?? "",
            Version = el.Element("version")?.Value ?? "",
            Author  = el.Element("author")?.Value  ?? "",
            Source  = el.Element("source")?.Value  ?? "",
            Enabled = ParseBool(el.Element("enabled")?.Value, true),
            Applied = ParseBool(el.Element("applied")?.Value, false),
        };
        var qars = el.Element("qars");
        if (qars is not null)
            foreach (var q in qars.Elements("qar"))
                if (!string.IsNullOrEmpty(q.Value)) m.QarPaths.Add(q.Value);

        var hashes = el.Element("qar_hashes");
        if (hashes is not null)
            foreach (var h in hashes.Elements("hash"))
            {
                var p = h.Attribute("path")?.Value ?? "";
                if (p.Length > 0) m.QarHashes[p] = h.Value;
            }

        var gd = el.Element("gamedir_entries");
        if (gd is not null)
            foreach (var g in gd.Elements("entry"))
                if (!string.IsNullOrEmpty(g.Value)) m.GameDirEntries.Add(g.Value);

        var tags = el.Element("tags");
        if (tags is not null)
            foreach (var t in tags.Elements("tag"))
                if (!string.IsNullOrEmpty(t.Value)) m.Tags.Add(t.Value);

        var fpks = el.Element("fpks");
        if (fpks is not null)
            foreach (var fpk in fpks.Elements("fpk"))
            {
                var host = fpk.Attribute("host")?.Value ?? "";
                if (host.Length == 0) continue;
                if (!m.FpkEntries.TryGetValue(host, out var list))
                    m.FpkEntries[host] = list = new();
                foreach (var inner in fpk.Elements("inner"))
                    if (!string.IsNullOrEmpty(inner.Value)) list.Add(inner.Value);
            }

        return m;
    }

    private static XElement WriteMod(ModInfo m)
    {
        var el = new XElement("mod",
            new XElement("id",      m.Id),
            new XElement("name",    m.Name),
            new XElement("version", m.Version),
            new XElement("author",  m.Author),
            new XElement("enabled", m.Enabled ? "true" : "false"),
            new XElement("applied", m.Applied ? "true" : "false"),
            new XElement("source",  m.Source));

        if (m.QarPaths.Count > 0)
        {
            var qars = new XElement("qars");
            foreach (var q in m.QarPaths) qars.Add(new XElement("qar", q));
            el.Add(qars);
        }

        if (m.QarHashes.Count > 0)
        {
            var hashes = new XElement("qar_hashes");
            foreach (var kv in m.QarHashes)
                hashes.Add(new XElement("hash", new XAttribute("path", kv.Key), kv.Value));
            el.Add(hashes);
        }

        if (m.GameDirEntries.Count > 0)
        {
            var gd = new XElement("gamedir_entries");
            foreach (var g in m.GameDirEntries) gd.Add(new XElement("entry", g));
            el.Add(gd);
        }

        if (m.Tags.Count > 0)
        {
            var tags = new XElement("tags");
            foreach (var t in m.Tags) tags.Add(new XElement("tag", t));
            el.Add(tags);
        }

        if (m.FpkEntries.Count > 0)
        {
            var fpks = new XElement("fpks");
            foreach (var kv in m.FpkEntries)
            {
                var fpk = new XElement("fpk", new XAttribute("host", kv.Key));
                foreach (var inner in kv.Value) fpk.Add(new XElement("inner", inner));
                fpks.Add(fpk);
            }
            el.Add(fpks);
        }

        return el;
    }

    // ─── Legacy state.txt migration ───────────────────────────────────────

    private static void LoadLegacyKv(State s, string path)
    {
        ModInfo? cur = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("game_root="))    { s.GameRoot = line[10..]; cur = null; continue; }
            if (line.StartsWith("datfpk="))       { s.DatFpk   = line[7..];  cur = null; continue; }
            if (line.StartsWith("hidecol="))      { var n = line[8..]; if (n.Length > 0) s.HiddenColumns.Add(n); cur = null; continue; }
            if (line.StartsWith("nexus_apikey=")) { s.NexusApiKey = line[13..]; cur = null; continue; }
            if (line.StartsWith("theme_mode="))   { s.Theme = ParseTheme(line[11..]); cur = null; continue; }
            if (line.StartsWith("theme="))        { s.Theme = line[6..] == "light" ? ThemeMode.Light : ThemeMode.Dark; cur = null; continue; }
            if (line.StartsWith("lang="))         { var v = line[5..]; if (v.Length > 0) s.Language = v; cur = null; continue; }
            if (line == "[mod]")                  { cur = new ModInfo(); s.Mods.Add(cur); continue; }
            if (cur is null) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var k = line[..eq]; var v2 = line[(eq + 1)..];
            switch (k)
            {
                case "id":      cur.Id = v2; break;
                case "name":    cur.Name = v2; break;
                case "version": cur.Version = v2; break;
                case "author":  cur.Author = v2; break;
                case "enabled": cur.Enabled = v2 == "1" || v2 == "true"; break;
                case "applied": cur.Applied = v2 == "1" || v2 == "true"; break;
                case "source":  cur.Source = v2; break;
                case "qar":     cur.QarPaths.Add(v2); break;
                case "qarhash": {
                    int bar = v2.IndexOf('|');
                    if (bar > 0) cur.QarHashes[v2[..bar]] = v2[(bar+1)..];
                    break;
                }
                case "gamedir": cur.GameDirEntries.Add(v2); break;
                case "tag":     if (v2.Length > 0) cur.Tags.Add(v2); break;
                case "fpk": {
                    int bar = v2.IndexOf('|');
                    if (bar > 0)
                    {
                        var host = v2[..bar]; var inner = v2[(bar+1)..];
                        if (!cur.FpkEntries.TryGetValue(host, out var list))
                            cur.FpkEntries[host] = list = new();
                        list.Add(inner);
                    }
                    break;
                }
            }
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static bool ParseBool(string? v, bool fallback)
    {
        if (string.IsNullOrEmpty(v)) return fallback;
        return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static ThemeMode ParseTheme(string v) => v switch
    {
        "light" => ThemeMode.Light,
        "auto"  => ThemeMode.Auto,
        _       => ThemeMode.Dark,
    };

    private static string ThemeString(ThemeMode m) => m switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Auto  => "auto",
        _               => "dark",
    };
}
