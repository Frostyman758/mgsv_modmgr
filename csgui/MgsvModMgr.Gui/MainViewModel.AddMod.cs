using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MgsvModMgr.Core;
using MgsvModMgr.Gui.Lang;

namespace MgsvModMgr.Gui;

// MainViewModel — Add Mod pipeline: file picker, drag-drop, archive
// extraction, per-file AddMod, failure roll-up.
public sealed partial class MainViewModel
{
    private async Task AddModAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = L.S("Str.AddMod.PickerTitle"),
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(L.S("Str.AddMod.FilterModsArchives"))
                {
                    Patterns = new[] { "*.mgsv", "*.zip", "*.rar", "*.7z", "*.tar", "*.tar.gz" },
                },
                new FilePickerFileType(L.S("Str.AddMod.FilterMods")) { Patterns = new[] { "*.mgsv" } },
                new FilePickerFileType(L.S("Str.AddMod.FilterArchives"))
                {
                    Patterns = new[] { "*.zip", "*.rar", "*.7z", "*.tar", "*.tar.gz" },
                },
            },
        });
        if (files.Count == 0) return;

        var pickedPaths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
        if (pickedPaths.Count == 0) return;

        await RunAddModPipelineAsync(pickedPaths);
    }

    /// <summary>
    /// Shared add-mod pipeline used by both the file-picker path and
    /// the window-wide drag-drop path.
    /// Accepts bare .mgsv files AND wrapper archives that mod authors
    /// ship on Nexus (zip / rar / 7z / tar / tar.gz). Expands the
    /// archives, calls AddMod for each contained .mgsv, narrates
    /// progress to the activity log, rolls failures into an error
    /// dialog at the end.
    /// </summary>
    private async Task RunAddModPipelineAsync(IReadOnlyList<string> pickedPaths)
    {
        // Expand archives into their contained .mgsv files. Bare .mgsv
        // entries pass through unchanged. Each archive's extracted
        // .mgsv files go into a unique temp dir we clean up at the end.
        var tempDirs = new List<string>();
        var paths    = new List<string>();
        foreach (var picked in pickedPaths)
        {
            if (picked.EndsWith(".mgsv", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(picked);
                continue;
            }
            try
            {
                var scratch = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "mgsv_modmgr_extract",
                    Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(scratch);
                tempDirs.Add(scratch);

                AppendLog($"Scanning {System.IO.Path.GetFileName(picked)} for .mgsv contents...");
                var found = await Task.Run(() => ExtractMgsvFiles(picked, scratch));
                if (found.Count == 0)
                {
                    AppendLog($"  no .mgsv files found inside {System.IO.Path.GetFileName(picked)}");
                    continue;
                }
                foreach (var f in found)
                    AppendLog($"  extracted {System.IO.Path.GetFileName(f)}");
                paths.AddRange(found);
            }
            catch (Exception ex)
            {
                AppendLog($"  ERROR extracting {System.IO.Path.GetFileName(picked)}: {ex.Message}");
            }
        }
        if (paths.Count == 0)
        {
            await ShowError(L.S("Str.AddMod.NoneTitle"),
                L.S("Str.AddMod.NoneBody"));
            CleanupTempDirs(tempDirs);
            return;
        }

        // Heavy I/O (zip extract + metadata.xml parse + dictionary update)
        // for each archive. state.txt and the two dictionary files are
        // append-only single-writer, so we install one mod at a time. The
        // loop body runs on the UI thread and awaits each archive's worker;
        // SyncRows then runs between archives, so the user sees each row
        // pop into the list as soon as it's installed instead of having
        // them all appear in one batch at the end.
        AppendLog(paths.Count == 1
            ? $"Adding mod from {System.IO.Path.GetFileName(paths[0])} ..."
            : $"Adding {paths.Count} mods ...");

        var failures = new List<(string Name, string Error)>();
        foreach (var path in paths)
        {
            if (paths.Count > 1)
                AppendLog($"  adding {System.IO.Path.GetFileName(path)}");
            try
            {
                await Task.Run(() => _manager.AddMod(path));
                SyncRows();
                MarkDirty();
            }
            catch (Exception ex)
            {
                failures.Add((System.IO.Path.GetFileName(path), ex.Message));
                AppendLog($"  ERROR adding {System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
        }
        CleanupTempDirs(tempDirs);

        if (failures.Count > 0)
        {
            var head    = failures.Take(8).Select(f => $"• {f.Name}: {f.Error}");
            var summary = string.Join("\n", head);
            if (failures.Count > 8)
                summary += "\n• " + L.F("Str.AddMod.MoreFailures", failures.Count - 8);
            await ShowError(
                L.F("Str.AddMod.FailedTitle", failures.Count, paths.Count),
                summary);
        }
    }

    /// <summary>
    /// Crack open a zip/rar/7z/tar archive and pull every .mgsv inside
    /// into a scratch directory. Delegates to the shared SharpCompress
    /// helper in NexusDownloader so the nxm download path and the
    /// manual Add Mod path share one extraction implementation.
    /// </summary>
    private static List<string> ExtractMgsvFiles(string archivePath, string destDir)
        => NexusDownloader.ExtractMgsvFiles(archivePath, destDir);

    /// <summary>
    /// Entry point for files dropped onto the window. Same downstream
    /// pipeline as the file-picker AddModAsync path — archive
    /// extraction, per-file AddMod, log narration, error rollup.
    /// </summary>
    public async Task AddDroppedFilesAsync(IReadOnlyList<string> pickedPaths)
    {
        if (pickedPaths.Count == 0) return;
        // Make sure the user sees what's happening — drop happens
        // anywhere on the window, so flip to the Mods page if not
        // already there.
        if (!IsModsPage && !IsLogPage) CurrentPage = Page.Mods;
        await RunAddModPipelineAsync(pickedPaths);
    }

    /// <summary>Best-effort cleanup; failures are ignored.</summary>
    private static void CleanupTempDirs(IEnumerable<string> dirs)
    {
        foreach (var d in dirs)
        {
            try { System.IO.Directory.Delete(d, recursive: true); } catch { }
        }
    }

}