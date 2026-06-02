using System.Xml.Linq;

namespace MgsvModMgr.Core;

public static class MetadataParser
{
    public static void Parse(string metaPath, ModInfo m)
    {
        var doc = XDocument.Load(metaPath);
        var root = doc.Root;
        if (root is null) return;

        m.Name    = (string?)root.Attribute("Name")    ?? m.Name;
        m.Version = (string?)root.Attribute("Version") ?? m.Version;
        m.Author  = (string?)root.Attribute("Author")  ?? m.Author;

        foreach (var e in root.Descendants().Where(x => x.Name.LocalName == "QarEntry"))
        {
            var p = (string?)e.Attribute("FilePath");
            if (string.IsNullOrEmpty(p)) continue;
            m.QarPaths.Add(p);
            var h = (string?)e.Attribute("Hash");
            if (!string.IsNullOrEmpty(h)) m.QarHashes[p] = h;
        }
        foreach (var e in root.Descendants().Where(x => x.Name.LocalName == "FpkEntry"))
        {
            var host  = (string?)e.Attribute("FpkFile");
            var inner = (string?)e.Attribute("FilePath");
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(inner)) continue;
            if (!m.FpkEntries.TryGetValue(host, out var list))
                m.FpkEntries[host] = list = new();
            list.Add(inner);
        }
        foreach (var e in root.Descendants().Where(x => x.Name.LocalName == "FileEntry"))
        {
            var p = (string?)e.Attribute("FilePath");
            if (string.IsNullOrEmpty(p)) continue;
            if (p[0] == '/') p = p[1..];
            m.GameDirEntries.Add(p);
        }
    }
}
