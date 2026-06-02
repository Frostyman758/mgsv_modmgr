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
        bool platform = last.EndsWith(".fpk") || last.EndsWith(".fpkd") || last.EndsWith(".pftxs");
        if (platform) outp.Add("#windx11");
        outp.Add(last);

        return Path.Combine(new[] { gameRoot }.Concat(outp).ToArray());
    }

    public static bool QarIsFpk(string qar)
        => qar.EndsWith(".fpk") || qar.EndsWith(".fpkd");
}
