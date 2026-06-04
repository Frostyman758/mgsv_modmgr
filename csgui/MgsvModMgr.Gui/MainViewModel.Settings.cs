using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MgsvModMgr.Gui.Lang;

namespace MgsvModMgr.Gui;

// MainViewModel — Settings page handlers (game root / datfpk pickers,
// save, reset apply state).
public sealed partial class MainViewModel
{
    // ─── Settings page ─────────────────────────────────────────────────────

    private void LoadSettingsFields()
    {
        GameRootField    = _manager.State.GameRoot;
        DatFpkField      = _manager.State.DatFpk;
        NexusApiKeyField = _manager.State.NexusApiKey;
    }

    private async Task BrowseGameRootAsync()
    {
        var folder = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.S("Str.Settings.GameRootPicker"),
        });
        var path = folder.Count > 0 ? folder[0].TryGetLocalPath() : null;
        if (path is not null) GameRootField = path;
    }

    private async Task BrowseDatFpkAsync()
    {
        // Cross-platform: Windows ships datfpk.exe, Linux/macOS ship a
        // bare `datfpk` binary. Allow either via the picker.
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = L.S("Str.Settings.DatFpkPicker"),
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("datfpk")                          { Patterns = new[] { "datfpk", "datfpk.exe" } },
                new FilePickerFileType(L.S("Str.Settings.FilterExecutables")) { Patterns = new[] { "*.exe", "*" } },
            },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null) DatFpkField = path;
    }

    private async Task ResetApplyStateAsync()
    {
        var ok = await ConfirmAsync(L.S("Str.Settings.ResetPrompt"));
        if (!ok) return;
        try
        {
            await Task.Run(() => _manager.ResetApplyState());
        }
        catch (Exception ex) { await ShowError(L.S("Str.Errors.ResetFailed"), ex.Message); }
    }

    private async Task SaveSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(GameRootField) || string.IsNullOrWhiteSpace(DatFpkField))
        {
            await ShowError(L.S("Str.Errors.SaveFailed"), L.S("Str.Errors.SaveFailedBody"));
            return;
        }
        try
        {
            _manager.Init(GameRootField.Trim(), DatFpkField.Trim());
            // Persist the Nexus key alongside the other settings. Empty
            // string is allowed — that just means "not signed in yet."
            var newKey = (NexusApiKeyField ?? "").Trim();
            var keyChanged = _manager.State.NexusApiKey != newKey;
            _manager.State.NexusApiKey = newKey;
            _manager.SaveState();
            // Invalidate Nexus caches if the key actually changed, so
            // the next nav to Nexus re-fetches with the new credential.
            if (keyChanged)
            {
                _allNexusMods.Clear();
                NexusMods.Clear();
                NexusError = "";
                _nexusTotalAvailable = 0;
            }
            OnPropertyChanged(nameof(GameRoot));
            OnPropertyChanged(nameof(DatFpk));
            OnPropertyChanged(nameof(NexusNeedsApiKey));
            CurrentPage = Page.Mods;
        }
        catch (Exception ex) { await ShowError(L.S("Str.Errors.SaveFailed"), ex.Message); }
    }
}