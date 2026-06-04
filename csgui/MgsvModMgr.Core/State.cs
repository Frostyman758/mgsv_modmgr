using System.Collections.ObjectModel;

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
    /// UI theme preference. Persisted to state.txt as
    /// <c>theme_mode=dark|light|auto</c>. The legacy <c>theme=dark|light</c>
    /// line is still recognised on read for backwards-compat with state
    /// files written before the Auto mode existed.
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
    /// Persisted in state.txt as one `hidecol=NAME` line per entry.
    /// </summary>
    public HashSet<string> HiddenColumns { get; } = new();
}

public static class StateIo
{
    public static void Load(State s, string statePath)
    {
        s.GameRoot = ""; s.DatFpk = ""; s.NexusApiKey = "";
        s.Theme = ThemeMode.Dark; s.Language = "en";
        s.Mods.Clear(); s.HiddenColumns.Clear();
        if (!File.Exists(statePath)) return;

        ModInfo? cur = null;
        foreach (var raw in File.ReadAllLines(statePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("game_root=")) { s.GameRoot = line[10..]; cur = null; continue; }
            if (line.StartsWith("datfpk="))    { s.DatFpk   = line[7..];  cur = null; continue; }
            if (line.StartsWith("hidecol="))   { var n = line[8..]; if (n.Length > 0) s.HiddenColumns.Add(n); cur = null; continue; }
            if (line.StartsWith("nexus_apikey=")) { s.NexusApiKey = line[13..]; cur = null; continue; }
            if (line.StartsWith("theme_mode="))   { s.Theme = ParseThemeMode(line[11..]); cur = null; continue; }
            // Backwards-compat: the old single-flag form. Auto didn't
            // exist when this was written, so we only see light/dark.
            if (line.StartsWith("theme="))        { s.Theme = line[6..] == "light" ? ThemeMode.Light : ThemeMode.Dark; cur = null; continue; }
            if (line.StartsWith("lang="))         { var v = line[5..]; if (v.Length > 0) s.Language = v; cur = null; continue; }
            if (line == "[mod]")               { cur = new ModInfo(); s.Mods.Add(cur); continue; }
            if (cur is null) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var k = line[..eq]; var v2 = line[(eq+1)..];
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

    private static ThemeMode ParseThemeMode(string v) => v switch
    {
        "light" => ThemeMode.Light,
        "auto"  => ThemeMode.Auto,
        _       => ThemeMode.Dark,
    };

    private static string ThemeModeString(ThemeMode m) => m switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Auto  => "auto",
        _               => "dark",
    };

    public static void Save(State s, string statePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        using var f = new StreamWriter(statePath, append: false);
        f.WriteLine("# mgsv_modmgr state. Edit load order by reordering [mod] blocks.");
        f.WriteLine($"game_root={s.GameRoot}");
        f.WriteLine($"datfpk={s.DatFpk}");
        foreach (var c in s.HiddenColumns) f.WriteLine($"hidecol={c}");
        if (!string.IsNullOrEmpty(s.NexusApiKey)) f.WriteLine($"nexus_apikey={s.NexusApiKey}");
        f.WriteLine($"theme_mode={ThemeModeString(s.Theme)}");
        f.WriteLine($"lang={s.Language}");
        foreach (var m in s.Mods)
        {
            f.WriteLine();
            f.WriteLine("[mod]");
            f.WriteLine($"id={m.Id}");
            f.WriteLine($"name={m.Name}");
            f.WriteLine($"version={m.Version}");
            f.WriteLine($"author={m.Author}");
            f.WriteLine($"enabled={(m.Enabled ? 1 : 0)}");
            f.WriteLine($"applied={(m.Applied ? 1 : 0)}");
            f.WriteLine($"source={m.Source}");
            foreach (var q in m.QarPaths) f.WriteLine($"qar={q}");
            foreach (var kv in m.QarHashes) f.WriteLine($"qarhash={kv.Key}|{kv.Value}");
            foreach (var g in m.GameDirEntries) f.WriteLine($"gamedir={g}");
            foreach (var t in m.Tags)           f.WriteLine($"tag={t}");
            foreach (var kv in m.FpkEntries)
                foreach (var i in kv.Value) f.WriteLine($"fpk={kv.Key}|{i}");
        }
    }
}
