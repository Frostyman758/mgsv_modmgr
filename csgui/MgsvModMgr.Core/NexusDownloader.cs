using System.Web;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace MgsvModMgr.Core;

/// <summary>
/// Glue between an incoming <c>nxm://</c> URL and our existing
/// mod-install flow. Three steps:
/// <list type="number">
///   <item>Parse the URL (mod id / file id / signed key + expires).</item>
///   <item>Exchange the signed key for a CDN download URL via the API,
///         then stream the file into a scratch dir.</item>
///   <item>If the file is a wrapped archive (rar / zip / 7z), peel out
///         any contained <c>.mgsv</c> files. Otherwise pass straight through.</item>
/// </list>
/// The downstream <see cref="ModManager.AddMod"/> path then handles
/// metadata + state.txt + the mods/ dir registration as usual.
/// </summary>
public static class NexusDownloader
{
    /// <summary>Parsed shape of an <c>nxm://</c> URL.</summary>
    public sealed record NxmUrl(string GameDomain, int ModId, int FileId, string? Key, long? Expires);

    /// <summary>
    /// Parses <c>nxm://metalgearsolidvtpp/mods/{id}/files/{id}?key=...&amp;expires=...&amp;user_id=...</c>.
    /// Returns null on any structural problem rather than throwing —
    /// caller decides how loudly to fail.
    /// </summary>
    public static NxmUrl? TryParse(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var u = new Uri(url);
            if (!string.Equals(u.Scheme, "nxm", StringComparison.OrdinalIgnoreCase)) return null;

            var game = u.Host;
            // Authority-as-game means the path is "/mods/{modId}/files/{fileId}".
            var segs = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length < 4) return null;
            if (!string.Equals(segs[0], "mods", StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(segs[2], "files", StringComparison.OrdinalIgnoreCase)) return null;
            if (!int.TryParse(segs[1], out var modId)) return null;
            if (!int.TryParse(segs[3], out var fileId)) return null;

            var q   = HttpUtility.ParseQueryString(u.Query);
            var key = q["key"];
            long? exp = long.TryParse(q["expires"], out var e) ? e : null;
            return new NxmUrl(game, modId, fileId, key, exp);
        }
        catch { return null; }
    }

    /// <summary>
    /// Full download → extract flow. Returns the list of <c>.mgsv</c>
    /// file paths that landed in <paramref name="destDir"/>, ready to
    /// be fed into <see cref="ModManager.AddMod"/>.
    /// </summary>
    public static async Task<List<string>> DownloadAndExtractAsync(
        NexusClient   client,
        NxmUrl        nxm,
        string        destDir,
        Action<string>? log = null)
    {
        Directory.CreateDirectory(destDir);

        log?.Invoke($"Nexus: exchanging download token for mod {nxm.ModId} file {nxm.FileId}...");
        var cdnUrl = await client.GetDownloadLinkAsync(nxm.ModId, nxm.FileId, nxm.Key, nxm.Expires);

        var fileName  = SafeFileNameFromUrl(cdnUrl);
        var tmpScratch = Path.Combine(destDir, "_nxm_tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpScratch);
        var downloaded = Path.Combine(tmpScratch, fileName);

        try
        {
            log?.Invoke($"Nexus: downloading {fileName}...");
            await client.DownloadFileAsync(cdnUrl, downloaded);

            // Bare .mgsv? Just copy it out, no extraction needed.
            if (fileName.EndsWith(".mgsv", StringComparison.OrdinalIgnoreCase))
            {
                var final = Path.Combine(destDir, fileName);
                File.Copy(downloaded, final, overwrite: true);
                log?.Invoke($"Nexus: ready {Path.GetFileName(final)}");
                return new List<string> { final };
            }

            // Wrapped archive — peel any .mgsv files out of it.
            log?.Invoke($"Nexus: scanning {fileName} for .mgsv contents...");
            var extracted = new List<string>();
            using (var archive = ArchiveFactory.Open(downloaded))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    var key = entry.Key ?? "";
                    if (!key.EndsWith(".mgsv", StringComparison.OrdinalIgnoreCase)) continue;

                    var outName = Path.GetFileName(key);
                    var outPath = Path.Combine(destDir, outName);
                    using (var es = entry.OpenEntryStream())
                    using (var fs = File.Create(outPath))
                        await es.CopyToAsync(fs);
                    extracted.Add(outPath);
                    log?.Invoke($"Nexus: extracted {outName}");
                }
            }
            if (extracted.Count == 0)
                throw new InvalidOperationException(
                    $"Archive {fileName} contains no .mgsv files. " +
                    "This mod might need manual install — open the page on Nexus.");
            return extracted;
        }
        finally
        {
            // Best-effort cleanup of scratch dir; ignore failure.
            try { Directory.Delete(tmpScratch, recursive: true); } catch { }
        }
    }

    private static string SafeFileNameFromUrl(string url)
    {
        var name = "download.bin";
        try
        {
            var u = new Uri(url);
            var last = u.AbsolutePath.Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last))
                name = Uri.UnescapeDataString(last);
        }
        catch { }
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return name;
    }
}
