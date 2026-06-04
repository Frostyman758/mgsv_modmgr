using System.Collections.ObjectModel;

namespace MgsvModMgr.Core;

public sealed class State
{
    public string GameRoot { get; set; } = "";
    public string DatFpk   { get; set; } = "";
    /// <summary>Personal API key from nexusmods.com/users/myaccount?tab=api.</summary>
    public string NexusApiKey { get; set; } = "";
    /// <summary>Persisted UI preference; false = dark, true = light.</summary>
    public bool IsLightTheme { get; set; }
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
        s.GameRoot = ""; s.DatFpk = ""; s.NexusApiKey = ""; s.IsLightTheme = false;
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
            if (line.StartsWith("theme="))        { s.IsLightTheme = line[6..] == "light"; cur = null; continue; }
            if (line == "[mod]")               { cur = new ModInfo(); s.Mods.Add(cur); continue; }
            if (cur is null) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var k = line[..eq]; var v = line[(eq+1)..];
            switch (k)
            {
                case "id":      cur.Id = v; break;
                case "name":    cur.Name = v; break;
                case "version": cur.Version = v; break;
                case "author":  cur.Author = v; break;
                case "enabled": cur.Enabled = v == "1" || v == "true"; break;
                case "applied": cur.Applied = v == "1" || v == "true"; break;
                case "source":  cur.Source = v; break;
                case "qar":     cur.QarPaths.Add(v); break;
                case "qarhash": {
                    int bar = v.IndexOf('|');
                    if (bar > 0) cur.QarHashes[v[..bar]] = v[(bar+1)..];
                    break;
                }
                case "gamedir": cur.GameDirEntries.Add(v); break;
                case "tag":     if (v.Length > 0) cur.Tags.Add(v); break;
                case "fpk": {
                    int bar = v.IndexOf('|');
                    if (bar > 0)
                    {
                        var host = v[..bar]; var inner = v[(bar+1)..];
                        if (!cur.FpkEntries.TryGetValue(host, out var list))
                            cur.FpkEntries[host] = list = new();
                        list.Add(inner);
                    }
                    break;
                }
            }
        }
    }

    public static void Save(State s, string statePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        using var f = new StreamWriter(statePath, append: false);
        f.WriteLine("# mgsv_modmgr state. Edit load order by reordering [mod] blocks.");
        f.WriteLine($"game_root={s.GameRoot}");
        f.WriteLine($"datfpk={s.DatFpk}");
        foreach (var c in s.HiddenColumns) f.WriteLine($"hidecol={c}");
        if (!string.IsNullOrEmpty(s.NexusApiKey)) f.WriteLine($"nexus_apikey={s.NexusApiKey}");
        f.WriteLine($"theme={(s.IsLightTheme ? "light" : "dark")}");
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
