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
        var platformDir = PlatformDirFor(last);
        if (platformDir is not null) outp.Add(platformDir);
        outp.Add(last);

        return Path.Combine(new[] { gameRoot }.Concat(outp).ToArray());
    }

    public static bool QarIsFpk(string qar)
        => qar.EndsWith(".fpk") || qar.EndsWith(".fpkd");

    /// <summary>
    /// Returns the per-platform subfolder name a file of this kind belongs
    /// in (e.g. <c>#windx11</c> for textures and packed archives, <c>#Win</c>
    /// for the audio engine's .sbp banks), or <c>null</c> if the file lives
    /// flat in its parent directory. Confirmed via procmon CreateFile output
    /// against the running mgsvtpp.exe.
    /// </summary>
    private static string? PlatformDirFor(string filename)
    {
        // #windx11/ : packed archives + textures (DirectX 11 mip streams).
        if (filename.EndsWith(".fpk")    ||
            filename.EndsWith(".fpkd")   ||
            filename.EndsWith(".pftxs")  ||
            filename.EndsWith(".ftex")   ||
            filename.EndsWith(".ftexs"))   // matches .1.ftexs, .2.ftexs, etc.
            return "#windx11";

        // #Win/ : the Wwise audio engine's runtime sound banks.
        if (filename.EndsWith(".sbp")    ||
            filename.EndsWith(".bnk")    ||
            filename.EndsWith(".wem"))
            return "#Win";

        return null;
    }
}
