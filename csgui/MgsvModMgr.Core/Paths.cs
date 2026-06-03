namespace MgsvModMgr.Core;

public static class Paths
{
    // Mirrors C++ resolve_qar_path:
    //   /Assets/<chunk>/<rest>   -> <game>/<chunk>/release/<...>/[#windx11/]<file>
    //   /<rest>                  -> <game>/<rest>
    public static string ResolveQar(string qar, string gameRoot)
    {
        qar = qar.Replace('\\', '/');
        if (string.IsNullOrEmpty(qar)) throw new ArgumentException("empty qar path");

        bool assets = qar.StartsWith("/Assets/") || qar.StartsWith("Assets/");
        if (!assets)
        {
            if (qar[0] == '/') qar = qar[1..];
            return Path.Combine(new[] { gameRoot }.Concat(qar.Split('/')).ToArray());
        }

        qar = qar.StartsWith("/Assets/") ? qar[8..] : qar[7..];
        var parts = qar.Split('/');
        if (parts.Length == 0) throw new ArgumentException("empty qar path under /Assets/");

        var outp = new List<string> { parts[0], "release" };
        for (int i = 1; i + 1 < parts.Length; ++i) outp.Add(parts[i]);

        var last = parts[^1];
        // Platform-routed files live under a per-platform subdir on disk.
        // For Windows that's #windx11/. Confirmed via procmon: the game's
        // CreateFile calls for loose .ftex headers and the matching .ftexs
        // mip streams target <chunk>/release/<rest>/#windx11/<file>, not the
        // flat path. Same for .fpk / .fpkd / .pftxs archives. .ftexs covers
        // every multi-dot variant (.1.ftexs, .2.ftexs, ...) via EndsWith.
        bool platform = last.EndsWith(".fpk")
                     || last.EndsWith(".fpkd")
                     || last.EndsWith(".pftxs")
                     || last.EndsWith(".ftex")
                     || last.EndsWith(".ftexs");
        if (platform) outp.Add("#windx11");
        outp.Add(last);

        return Path.Combine(new[] { gameRoot }.Concat(outp).ToArray());
    }

    public static bool QarIsFpk(string qar)
        => qar.EndsWith(".fpk") || qar.EndsWith(".fpkd");
}
