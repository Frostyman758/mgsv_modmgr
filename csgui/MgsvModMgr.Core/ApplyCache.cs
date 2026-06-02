using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MgsvModMgr.Core;

/// <summary>
/// Per-host fingerprint cache. A host fpk/raw file only needs to be rebuilt
/// if any of its inputs changed since the last successful Apply. The
/// fingerprint captures:
///   - the QAR path,
///   - each enabled contributor in load order (mod id + .mgsv archive size
///     and mtime — cheap to read, monotonic under updates),
///   - the baseline file's size and mtime (changes if the user replaced
///     their game install).
/// Persisted to <c>apply-cache.txt</c> sibling of state.txt as plain
/// <c>diskPath = sha256hex</c> lines.
/// </summary>
public sealed class ApplyCache
{
    private readonly string _path;
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ApplyCache(string cacheFilePath)
    {
        _path = cacheFilePath;
        Load();
    }

    public void Load()
    {
        _entries.Clear();
        if (!File.Exists(_path)) return;
        foreach (var raw in File.ReadAllLines(_path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            _entries[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var f = new StreamWriter(_path, append: false);
        f.WriteLine("# mgsv_modmgr apply-cache. Per-host fingerprints; safe to delete to force full rebuild.");
        foreach (var kv in _entries) f.WriteLine($"{kv.Key}={kv.Value}");
    }

    /// <summary>True if a previous Apply wrote this exact fingerprint for this host.</summary>
    public bool Matches(string diskPath, string fingerprint)
        => _entries.TryGetValue(diskPath, out var stored) && stored == fingerprint;

    public void Set(string diskPath, string fingerprint) => _entries[diskPath] = fingerprint;
    public void Invalidate(string diskPath) => _entries.Remove(diskPath);
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Build a fingerprint from the inputs that determine a host's content.
    /// Same inputs in the same order → same fingerprint, so the cached output
    /// is still valid.
    /// </summary>
    public static string Compute(string qarPath, IEnumerable<(ModInfo mod, string archivePath)> contributors, string? baselinePath)
    {
        var sb = new StringBuilder(256);
        sb.Append(qarPath).Append('\n');

        foreach (var (mod, archive) in contributors)
        {
            sb.Append(mod.Id).Append('|');
            if (File.Exists(archive))
            {
                var fi = new FileInfo(archive);
                sb.Append(fi.Length.ToString(CultureInfo.InvariantCulture))
                  .Append('|')
                  .Append(fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("missing");
            }
            sb.Append('\n');
        }

        sb.Append("baseline|");
        if (!string.IsNullOrEmpty(baselinePath) && File.Exists(baselinePath))
        {
            var fi = new FileInfo(baselinePath);
            sb.Append(fi.Length.ToString(CultureInfo.InvariantCulture))
              .Append('|')
              .Append(fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append("absent");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
