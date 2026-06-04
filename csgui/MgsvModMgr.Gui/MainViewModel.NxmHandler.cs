using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using MgsvModMgr.Core;

namespace MgsvModMgr.Gui;

// MainViewModel — nxm:// protocol-handler glue.
// Receives URLs from Program.Main (cold-start arg) and from a
// second-instance handoff via nxm_inbox.txt, then drives the
// download / archive-extract / AddMod pipeline.
public sealed partial class MainViewModel
{

    // ─── nxm:// protocol handler ─────────────────────────────────
    /// <summary>
    /// Subscribes to two sources of incoming <c>nxm://</c> URLs:
    /// one-shot env var set by Program.Main when the app cold-starts
    /// from a protocol click, and a FileSystemWatcher on nxm_inbox.txt
    /// for handoffs from a second instance fired while we're running.
    /// </summary>
    private void StartNxmListener()
    {
        var pending = Environment.GetEnvironmentVariable("MGSV_PENDING_NXM");
        if (!string.IsNullOrWhiteSpace(pending))
        {
            Environment.SetEnvironmentVariable("MGSV_PENDING_NXM", null);
            _ = HandleNxmUrlAsync(pending);
        }

        try
        {
            var dir = AppContext.BaseDirectory;
            var fsw = new FileSystemWatcher(dir, "nxm_inbox.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) =>
            {
                try
                {
                    var path = Program.NxmInboxPath;
                    if (!File.Exists(path)) return;
                    var url = File.ReadAllText(path).Trim();
                    File.Delete(path);
                    if (!string.IsNullOrWhiteSpace(url))
                        Avalonia.Threading.Dispatcher.UIThread.Post(
                            () => _ = HandleNxmUrlAsync(url));
                }
                catch { }
            };
            fsw.Changed += h;
            fsw.Created += h;
        }
        catch { /* watcher is best-effort */ }
    }

    /// <summary>
    /// End-to-end: parse the URL, exchange the token, stream the
    /// archive, peel out any <c>.mgsv</c> files, then drop them into
    /// the existing mod-install pipeline. Errors land in the activity
    /// log; the page navigates to it so the user sees progress.
    /// </summary>
    private async Task HandleNxmUrlAsync(string url)
    {
        var key = _manager.State.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            AppendLog("Nexus: received a download URL but no API key is configured. Open Settings to paste your key.");
            CurrentPage = Page.Settings;
            return;
        }
        var nxm = NexusDownloader.TryParse(url);
        if (nxm is null)
        {
            AppendLog($"Nexus: ignored malformed download URL: {url}");
            return;
        }

        CurrentPage = Page.Log;
        AppendLog($"Nexus: incoming download {url}");
        try
        {
            var dropDir = Path.Combine(Path.GetTempPath(), "mgsv_modmgr_nxm");
            var client  = new NexusClient(key);
            var files   = await Task.Run(() =>
                NexusDownloader.DownloadAndExtractAsync(client, nxm, dropDir, AppendLog));

            // Hand each extracted .mgsv to the existing add-mod pipeline.
            foreach (var f in files)
            {
                try
                {
                    await Task.Run(() => _manager.AddMod(f));
                    AppendLog($"Nexus: installed {Path.GetFileName(f)}");
                }
                catch (Exception ex)
                {
                    AppendLog($"Nexus: add-mod failed for {Path.GetFileName(f)}: {ex.Message}");
                }
                finally
                {
                    try { File.Delete(f); } catch { }
                }
            }
            SyncRows();
            MarkDirty();
        }
        catch (Exception ex)
        {
            AppendLog($"Nexus: download failed: {ex.Message}");
        }
    }
}