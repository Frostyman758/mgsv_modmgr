using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MgsvModMgr.Core;
using MgsvModMgr.Gui.Commands;

namespace MgsvModMgr.Gui;

/// <summary>
/// Top-level view-model for <see cref="MainWindow"/>. Wraps a
/// <see cref="ModManager"/> and projects its state into observable
/// collections and commands suitable for binding.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Window     _window;
    private readonly ModManager _manager;

    public MainViewModel(Window window)
    {
        _window  = window;
        _manager = new ModManager { Log = AppendLog };
        _manager.LoadState();

        AddCommand           = new RelayCommand(AddModAsync);
        RemoveCommand        = new RelayCommand(RemoveSelectedAsync);
        MoveUpCommand        = new RelayCommand(() => Move(-1));
        MoveDownCommand      = new RelayCommand(() => Move(+1));
        ApplyCommand         = new RelayCommand(ApplyAsync);
        RevertCommand        = new RelayCommand(RevertAsync);
        AboutCommand         = new RelayCommand(ShowAbout);
        ToggleLogCommand     = new RelayCommand(() => LogExpanded = !LogExpanded);
        ExportDictCommand    = new RelayCommand(ExportDictionariesAsync);

        // Page navigation.
        ShowModsCommand     = new RelayCommand(() => CurrentPage = Page.Mods);
        ShowSettingsCommand = new RelayCommand(() => { LoadSettingsFields(); CurrentPage = Page.Settings; });

        // Settings page.
        BrowseGameRootCommand = new RelayCommand(BrowseGameRootAsync);
        BrowseDatFpkCommand   = new RelayCommand(BrowseDatFpkAsync);
        SaveSettingsCommand   = new RelayCommand(SaveSettingsAsync);

        RemoveRowCommand = new RelayCommand<ModRow>(async row =>
        {
            if (row is null) return;
            SelectedMod = row;
            await RemoveSelectedAsync();
        });

        ToggleRowCommand = new RelayCommand<ModRow>(row =>
        {
            if (row is not null) row.Enabled = !row.Enabled;
        });

        MoveRowCommand = new RelayCommand<MoveArg>(arg =>
        {
            if (arg?.Row is null) return;
            SelectedMod = arg.Row;
            Move(arg.Delta);
        });

        SyncRows();
    }

    // ─── Bound state ───────────────────────────────────────────────────────

    /// <summary>Rows bound by the main list.</summary>
    public ObservableCollection<ModRow> Mods { get; } = new();

    private ModRow? _selectedMod;
    public  ModRow? SelectedMod { get => _selectedMod; set => Set(ref _selectedMod, value); }

    private string _logText = "";
    public  string  LogText  { get => _logText; private set => Set(ref _logText, value); }

    private bool _logExpanded;
    public  bool  LogExpanded { get => _logExpanded; set => Set(ref _logExpanded, value); }

    /// <summary>
    /// True when the user has made changes (add / remove / toggle / move)
    /// that have not yet been written to the game install via Apply. Bound
    /// to the sidebar Apply button's <c>dirty</c> class to drive a pulse.
    /// </summary>
    private bool _isDirty;
    public  bool  IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }

    private void MarkDirty() => IsDirty = true;

    /// <summary>Which content page the main area is showing.</summary>
    private Page _currentPage = Page.Mods;
    public  Page  CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == value) return;
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModsPage));
            OnPropertyChanged(nameof(IsSettingsPage));
        }
    }

    public bool IsModsPage     => CurrentPage == Page.Mods;
    public bool IsSettingsPage => CurrentPage == Page.Settings;

    // Settings form fields. Bound two-way; the running manager state isn't
    // touched until Save fires.
    private string _gameRootField = "";
    public  string  GameRootField { get => _gameRootField; set => Set(ref _gameRootField, value); }

    private string _datFpkField = "";
    public  string  DatFpkField   { get => _datFpkField;   set => Set(ref _datFpkField,   value); }

    public string GameRoot => _manager.State.GameRoot;
    public string DatFpk   => _manager.State.DatFpk;

    public string HeaderSubtitle => Mods.Count switch
    {
        0 => "No mods installed yet. Click '+ Add mod' to get started.",
        1 => "1 mod installed.",
        _ => $"{Mods.Count} mods installed.",
    };

    // ─── Commands ──────────────────────────────────────────────────────────

    public ICommand AddCommand           { get; }
    public ICommand RemoveCommand        { get; }
    public ICommand MoveUpCommand        { get; }
    public ICommand MoveDownCommand      { get; }
    public ICommand ApplyCommand         { get; }
    public ICommand RevertCommand        { get; }
    public ICommand AboutCommand         { get; }
    public ICommand ToggleLogCommand     { get; }
    public ICommand ExportDictCommand    { get; }

    /// <summary>Page-navigation commands bound to the sidebar.</summary>
    public ICommand ShowModsCommand     { get; }
    public ICommand ShowSettingsCommand { get; }

    /// <summary>Settings-page commands.</summary>
    public ICommand BrowseGameRootCommand { get; }
    public ICommand BrowseDatFpkCommand   { get; }
    public ICommand SaveSettingsCommand   { get; }

    /// <summary>Row-targeted variants for the right-click context menu.</summary>
    public ICommand RemoveRowCommand { get; }
    public ICommand ToggleRowCommand { get; }
    public ICommand MoveRowCommand   { get; }

    // ─── Lifecycle ─────────────────────────────────────────────────────────

    public Task OnOpenedAsync()
    {
        if (string.IsNullOrEmpty(_manager.State.GameRoot) ||
            string.IsNullOrEmpty(_manager.State.DatFpk))
        {
            AppendLog("Not initialised. Set the game root and datfpk path on the Settings page.");
            LoadSettingsFields();
            CurrentPage = Page.Settings;
        }
        return Task.CompletedTask;
    }

    // ─── Mod-list sync ─────────────────────────────────────────────────────

    private void SyncRows()
    {
        Mods.Clear();
        foreach (var mod in _manager.State.Mods)
        {
            Mods.Add(new ModRow(mod, enabled =>
            {
                try
                {
                    _manager.EnableMod(mod.Id, enabled);
                    MarkDirty();
                }
                catch (Exception ex) { AppendLog("ERROR: " + ex.Message); }
            }));
        }
        OnPropertyChanged(nameof(HeaderSubtitle));
    }

    // ─── Command handlers ──────────────────────────────────────────────────

    private async Task AddModAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Select .mgsv mod",
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SnakeBite mods") { Patterns = new[] { "*.mgsv" } },
            },
        });
        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        try
        {
            _manager.AddMod(path);
            SyncRows();
            MarkDirty();
        }
        catch (Exception ex) { await ShowError("Add failed", ex.Message); }
    }

    private async Task RemoveSelectedAsync()
    {
        if (SelectedMod is null) return;
        var id = SelectedMod.Id;

        var confirmed = await ConfirmAsync(
            $"Remove mod '{id}'?\n\nRun Apply afterwards to rebuild without it.");
        if (!confirmed) return;

        try
        {
            _manager.RemoveMod(id);
            SyncRows();
            MarkDirty();
        }
        catch (Exception ex) { await ShowError("Remove failed", ex.Message); }
    }

    private void Move(int delta)
    {
        if (SelectedMod is null) return;
        var id = SelectedMod.Id;
        try
        {
            _manager.MoveMod(id, delta);
            SyncRows();
            SelectedMod = Mods.FirstOrDefault(r => r.Id == id);
            MarkDirty();
        }
        catch (Exception ex) { AppendLog("ERROR: " + ex.Message); }
    }

    private async Task ApplyAsync()
    {
        await Task.Run(() =>
        {
            try { _manager.ApplyAll(); }
            catch (Exception ex) { AppendLog("ERROR: " + ex.Message); }
        });
        IsDirty = false;
    }

    private async Task RevertAsync()
    {
        if (!await ConfirmAsync("Revert all modded files to their backups?")) return;
        await Task.Run(() =>
        {
            try { _manager.RevertAll(); }
            catch (Exception ex) { AppendLog("ERROR: " + ex.Message); }
        });
        IsDirty = false;
    }

    // ─── Settings page ─────────────────────────────────────────────────────

    private void LoadSettingsFields()
    {
        GameRootField = _manager.State.GameRoot;
        DatFpkField   = _manager.State.DatFpk;
    }

    private async Task BrowseGameRootAsync()
    {
        var folder = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select MGSV:TPP game root",
        });
        var path = folder.Count > 0 ? folder[0].TryGetLocalPath() : null;
        if (path is not null) GameRootField = path;
    }

    private async Task BrowseDatFpkAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Select datfpk.exe",
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executables") { Patterns = new[] { "*.exe" } },
            },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null) DatFpkField = path;
    }

    private async Task SaveSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(GameRootField) || string.IsNullOrWhiteSpace(DatFpkField))
        {
            await ShowError("Save failed", "Both Game root and datfpk paths are required.");
            return;
        }
        try
        {
            _manager.Init(GameRootField.Trim(), DatFpkField.Trim());
            OnPropertyChanged(nameof(GameRoot));
            OnPropertyChanged(nameof(DatFpk));
            CurrentPage = Page.Mods;
        }
        catch (Exception ex) { await ShowError("Save failed", ex.Message); }
    }

    private Task ShowAbout()
    {
        AppendLog("mgsv_modmgr -- Avalonia front-end.");
        AppendLog("PathDictionary.txt and ExplicitPathDictionary.txt are auto-maintained next to the game exe.");
        return Task.CompletedTask;
    }

    private async Task ExportDictionariesAsync()
    {
        if (string.IsNullOrEmpty(_manager.State.GameRoot))
        {
            await ShowError("Cannot export", "Game root is not set. Use the gear icon first.");
            return;
        }
        try
        {
            var added = await Task.Run(() => _manager.RebuildDictionary());
            AppendLog($"Dictionary export: +{added} new entries written to {_manager.State.GameRoot}");
        }
        catch (Exception ex) { await ShowError("Export failed", ex.Message); }
    }

    // ─── Logging ───────────────────────────────────────────────────────────

    private readonly StringBuilder _logBuffer = new();

    private void AppendLog(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logBuffer.AppendLine(line);
            LogText = _logBuffer.ToString();
            if (!LogExpanded) LogExpanded = true;
        });
    }

    // ─── Dialog helpers ────────────────────────────────────────────────────

    private Task<bool> ConfirmAsync(string body)
        => new ConfirmDialog(body).ShowDialog<bool>(_window);

    private Task ShowError(string title, string body)
        => new ConfirmDialog(body, title, errorMode: true).ShowDialog<bool>(_window);

    // ─── INotifyPropertyChanged ────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
