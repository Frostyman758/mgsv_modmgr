using System;
using System.Collections.Concurrent;
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
using MgsvModMgr.Gui.Lang;

namespace MgsvModMgr.Gui;

/// <summary>
/// Top-level view-model for <see cref="MainWindow"/>. Wraps a
/// <see cref="ModManager"/> and projects its state into observable
/// collections and commands suitable for binding.
/// </summary>
public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly Window     _window;
    private readonly ModManager _manager;

    public MainViewModel(Window window)
    {
        _window  = window;
        _manager = new ModManager
        {
            Log             = AppendLog,
            ApplyProgressed = p => Dispatcher.UIThread.Post(() => ApplyProgress = p),
        };
        _manager.LoadState();

        // Drain queued log lines onto the UI thread on a fixed cadence rather
        // than per-line. Big Applies (Infinite Heaven, etc.) emit thousands of
        // lines; updating the bound LogText string per line would be O(N^2)
        // and saturate the dispatcher queue, softlocking the window.
        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80),
                                        DispatcherPriority.Background,
                                        (_, _) => FlushLogQueue());
        _logTimer.Start();

        AddCommand           = new RelayCommand(AddModAsync);
        RemoveCommand        = new RelayCommand(RemoveSelectedAsync);
        MoveUpCommand        = new RelayCommand(() => Move(-1));
        MoveDownCommand      = new RelayCommand(() => Move(+1));
        ApplyCommand         = new RelayCommand(ApplyAsync);
        RevertCommand        = new RelayCommand(RevertAsync);
        AboutCommand         = new RelayCommand(ShowAbout);
        ToggleLogCommand     = new RelayCommand(() => CurrentPage = IsLogPage ? Page.Mods : Page.Log);
        ExportDictCommand    = new RelayCommand(ExportDictionariesAsync);

        // Page navigation.
        ShowModsCommand     = new RelayCommand(() => CurrentPage = Page.Mods);
        ShowNexusCommand    = new RelayCommand(() => { EnsureNexusSeeded(); CurrentPage = Page.Nexus; });
        ShowSettingsCommand = new RelayCommand(() => { LoadSettingsFields(); CurrentPage = Page.Settings; });
        ClearNexusSelectionCommand    = new RelayCommand(() => NexusSelectedMod = null);
        OpenNexusInBrowserCommand     = new RelayCommand(OpenSelectedNexusInBrowser);
        SetTrendingFilterCommand      = new RelayCommand(() => { CurrentNexusFilter = NexusFilter.Trending;      _ = LoadNexusAsync(); });
        SetLatestAddedFilterCommand   = new RelayCommand(() => { CurrentNexusFilter = NexusFilter.LatestAdded;   _ = LoadNexusAsync(); });
        SetLatestUpdatedFilterCommand = new RelayCommand(() => { CurrentNexusFilter = NexusFilter.LatestUpdated; _ = LoadNexusAsync(); });
        RefreshNexusCommand           = new RelayCommand(() => _ = LoadNexusAsync());
        OpenNexusFilesPageCommand     = new RelayCommand(OpenSelectedNexusFilesPage);
        NexusSsoSignInCommand         = new RelayCommand(SignInWithNexusAsync);
        FilterByCurrentAuthorCommand   = new RelayCommand(FilterByCurrentAuthor);
        FilterByCurrentCategoryCommand = new RelayCommand(FilterByCurrentCategory);
        ClearFacetFiltersCommand       = new RelayCommand(ClearFacetFilters);

        // Settings page.
        BrowseGameRootCommand = new RelayCommand(BrowseGameRootAsync);
        BrowseDatFpkCommand   = new RelayCommand(BrowseDatFpkAsync);
        SaveSettingsCommand   = new RelayCommand(SaveSettingsAsync);
        ResetApplyStateCommand = new RelayCommand(ResetApplyStateAsync);

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

    /// <summary>
    /// File-path conflicts among currently-enabled mods, refreshed
    /// after any state change (add / remove / toggle / reorder /
    /// apply). Drives the lower half of the Log page.
    /// </summary>
    public ObservableCollection<ConflictRowVm> Conflicts { get; } = new();
    public bool   HasConflicts        => Conflicts.Count > 0;
    public string ConflictsCountLabel =>
        Conflicts.Count switch
        {
            0 => "No file conflicts in the current load order.",
            1 => "1 file conflict",
            var n => $"{n} file conflicts",
        };

    private void RefreshConflicts()
    {
        Conflicts.Clear();
        foreach (var c in _manager.DetectConflicts())
            Conflicts.Add(ConflictRowVm.From(c));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ConflictsCountLabel));
    }

    private ModRow? _selectedMod;
    public  ModRow? SelectedMod { get => _selectedMod; set => Set(ref _selectedMod, value); }

    // Bound by the toolbar search pill. Filter wiring (applying this
    // against the visible Mods collection) is a follow-up — interacts
    // with drag-drop index translation so deserves its own pass.
    private string _searchText = "";
    public  string  SearchText { get => _searchText; set => Set(ref _searchText, value); }

    /// <summary>
    /// Persisted UI prefs: column headers the user has hidden via the
    /// header context menu. Read at startup to apply, written on toggle.
    /// </summary>
    public IReadOnlyCollection<string> HiddenColumns => _manager.State.HiddenColumns;
    public bool IsColumnHidden(string header) => _manager.State.HiddenColumns.Contains(header);
    public void SetColumnHidden(string header, bool hidden)
    {
        var set = _manager.State.HiddenColumns;
        var changed = hidden ? set.Add(header) : set.Remove(header);
        if (changed) _manager.SaveState();
    }

    private string _logText = "";
    public  string  LogText  { get => _logText; private set => Set(ref _logText, value); }


    /// <summary>
    /// True when the user has made changes (add / remove / toggle / move)
    /// that have not yet been written to the game install via Apply. Bound
    /// to the sidebar Apply button's <c>dirty</c> class to drive a pulse.
    /// </summary>
    private bool _isDirty;
    public  bool  IsDirty
    {
        get => _isDirty;
        private set { if (Set(ref _isDirty, value)) OnPropertyChanged(nameof(DirtyMix)); }
    }

    private void MarkDirty()
    {
        IsDirty = true;
        // Toggles, reorders, and Apply all funnel through MarkDirty
        // (without going through SyncRows), so refresh the conflict
        // list here too so the Log page's lower half tracks live.
        RefreshConflicts();
    }

    /// <summary>Which content page the main area is showing.</summary>
    private Page _currentPage = Page.Mods;
    public  Page  CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == value) return;
            // Free the Nexus card thumbnails on the way out so a long
            // browsing session doesn't permanently camp on hundreds of
            // megabytes of decoded image data after the user leaves
            // for the install list.
            if (_currentPage == Page.Nexus && value != Page.Nexus)
                ClearNexusCache();
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModsPage));
            OnPropertyChanged(nameof(IsNexusPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsLogPage));
            OnPropertyChanged(nameof(IsAwayFromInstallList));
        }
    }

    // ── Apply progress (drives the bottom progress bar) ──────────────────
    private bool _isApplying;
    public  bool  IsApplying
    {
        get => _isApplying;
        private set { if (Set(ref _isApplying, value)) { OnPropertyChanged(nameof(DirtyMix)); OnPropertyChanged(nameof(IsFooterShown)); } }
    }

    private double _applyProgress;
    public  double ApplyProgress
    {
        get => _applyProgress;
        private set { if (Set(ref _applyProgress, value)) OnPropertyChanged(nameof(DirtyMix)); }
    }

    /// <summary>
    /// 1.0 == fully amber (pending), 0.0 == fully red (applied). During Apply,
    /// tracks (1 - ApplyProgress) so the colour bleed across the UI marches
    /// with the install bar. Idle: just mirrors IsDirty as a bool. Used as
    /// the binding source for every control whose tint we want synced.
    /// </summary>
    public double DirtyMix
    {
        get
        {
            if (IsApplying) return Math.Max(0.0, 1.0 - ApplyProgress);
            return IsDirty ? 1.0 : 0.0;
        }
    }

    // ── Footer (game root / datfpk strip) visibility ─────────────────────
    private bool _userHidFooter;
    /// <summary>
    /// True while the footer should be on screen. The user can click the
    /// footer to hide it; an in-flight Apply force-shows it regardless of
    /// their preference.
    /// </summary>
    public bool IsFooterShown => IsApplying || !_userHidFooter;

    public void HideFooter()
    {
        if (_userHidFooter) return;
        _userHidFooter = true;
        OnPropertyChanged(nameof(IsFooterShown));
    }

    public bool IsModsPage     => CurrentPage == Page.Mods;
    public bool IsNexusPage    => CurrentPage == Page.Nexus;
    public bool IsSettingsPage => CurrentPage == Page.Settings;
    public bool IsLogPage      => CurrentPage == Page.Log;
    /// <summary>
    /// True when the user is on a "non install-list" page — Nexus
    /// browser or Settings. Drives the sidebar collapse so the
    /// Apply/Revert/Log/ExportDict pip strip slides away on either of
    /// those pages instead of just Nexus. Switching between Nexus and
    /// Settings doesn't flicker the strip back into view, because the
    /// predicate stays true across the whole non-install-list trip.
    /// </summary>
    public bool IsAwayFromInstallList => IsNexusPage || IsSettingsPage;

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
        0 => L.S("Str.Subtitle.None"),
        1 => L.S("Str.Subtitle.One"),
        _ => L.F("Str.Subtitle.Many", Mods.Count),
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
    public ICommand ShowModsCommand                { get; }
    public ICommand ShowNexusCommand               { get; }
    public ICommand ShowSettingsCommand            { get; }
    public ICommand ClearNexusSelectionCommand     { get; }
    public ICommand OpenNexusInBrowserCommand      { get; }
    public ICommand SetTrendingFilterCommand       { get; }
    public ICommand SetLatestAddedFilterCommand    { get; }
    public ICommand SetLatestUpdatedFilterCommand  { get; }
    public ICommand RefreshNexusCommand            { get; }
    public ICommand OpenNexusFilesPageCommand      { get; }
    public ICommand NexusSsoSignInCommand          { get; }
    public ICommand FilterByCurrentAuthorCommand   { get; }
    public ICommand FilterByCurrentCategoryCommand { get; }
    public ICommand ClearFacetFiltersCommand       { get; }


    /// <summary>Settings-page commands.</summary>
    public ICommand BrowseGameRootCommand { get; }
    public ICommand BrowseDatFpkCommand   { get; }
    public ICommand SaveSettingsCommand   { get; }
    public ICommand ResetApplyStateCommand { get; }

    /// <summary>Row-targeted variants for the right-click context menu.</summary>
    public ICommand RemoveRowCommand { get; }
    public ICommand ToggleRowCommand { get; }
    public ICommand MoveRowCommand   { get; }

    // ─── Lifecycle ─────────────────────────────────────────────────────────

    public Task OnOpenedAsync()
    {
        // Apply persisted theme as the very first paint — before any
        // visible content settles in — so the user never sees a flash
        // of the default variant.
        ApplyThemeVariant();

        if (string.IsNullOrEmpty(_manager.State.GameRoot) ||
            string.IsNullOrEmpty(_manager.State.DatFpk))
        {
            AppendLog("Not initialised. Set the game root and datfpk path on the Settings page.");
            LoadSettingsFields();
            CurrentPage = Page.Settings;
        }

        // nxm:// handoff: if we were launched directly with a URL, the
        // Program env var holds it. If we were already running and the
        // second instance dropped a URL into nxm_inbox.txt, we pick it
        // up via the file watcher. Both feed into the same handler.
        StartNxmListener();

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
        // Conflicts depend on which mods are present + their order +
        // their enabled flag. SyncRows fires on every mutation that
        // touches any of those, so this is the right central spot.
        RefreshConflicts();
    }

    // ─── Command handlers ──────────────────────────────────────────────────

    private async Task RemoveSelectedAsync()
    {
        if (SelectedMod is null) return;
        var id = SelectedMod.Id;

        var confirmed = await ConfirmAsync(
            $"Remove mod '{id}'?\n\nRun Apply afterwards to rebuild without it.");
        if (!confirmed) return;

        try
        {
            await Task.Run(() => _manager.RemoveMod(id));
            SyncRows();
            MarkDirty();
        }
        catch (Exception ex) { await ShowError(L.S("Str.Errors.RemoveFailed"), ex.Message); }
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

    /// <summary>Called by the drag-reorder handler in the code-behind.</summary>
    public void MoveRowToIndex(string id, int newIndex)
    {
        if (IsApplying) return;
        try
        {
            _manager.MoveModToIndex(id, newIndex);
            SyncRows();
            SelectedMod = Mods.FirstOrDefault(r => r.Id == id);
            MarkDirty();
        }
        catch (Exception ex) { AppendLog("ERROR: " + ex.Message); }
    }

    private async Task ApplyAsync()
    {
        if (IsApplying) return;
        IsApplying    = true;
        ApplyProgress = 0;
        try
        {
            await Task.Run(() =>
            {
                try { _manager.ApplyAll(); }
                catch (Exception ex) { AppendLog("ERROR: " + ex.Message); }
            });
            SyncRows();          // refresh PENDING chips
            IsDirty = false;
        }
        finally
        {
            ApplyProgress = 1;
            IsApplying    = false;
        }
    }

    private async Task RevertAsync()
    {
        if (!await ConfirmAsync("Restore the original game files? Every mod change in the install will be undone.")) return;
        await Task.Run(() =>
        {
            try { _manager.RevertAll(); }
            catch (Exception ex) { AppendLog("ERROR: " + ex.Message); }
        });
        SyncRows();
        IsDirty = false;
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
            await ShowError(L.S("Str.Errors.CannotExport"), L.S("Str.Errors.CannotExportBody"));
            return;
        }
        try
        {
            var added = await Task.Run(() => _manager.RebuildDictionary());
            AppendLog($"Dictionary export: +{added} new entries written to {_manager.State.GameRoot}");
        }
        catch (Exception ex) { await ShowError(L.S("Str.Errors.ExportFailed"), ex.Message); }
    }

    // ─── Logging ───────────────────────────────────────────────────────────
    //
    // Producer side (any thread): AppendLog enqueues a line. No marshaling.
    // Consumer side (UI thread):  the dispatcher timer below drains the queue
    // in batches and updates the bound LogText property once per tick. The
    // visible buffer is capped at ~256 KB; once exceeded, the oldest lines
    // are dropped so the TextBox never has to reflow a multi-MB string.

    private const int MaxLogChars = 256_000;
    private readonly StringBuilder            _logBuffer = new();
    private readonly ConcurrentQueue<string>  _logQueue  = new();
    private readonly DispatcherTimer          _logTimer;

    private void AppendLog(string line) => _logQueue.Enqueue(line);

    private void FlushLogQueue()
    {
        if (_logQueue.IsEmpty) return;

        while (_logQueue.TryDequeue(out var line))
        {
            _logBuffer.Append(line);
            _logBuffer.Append('\n');
        }

        if (_logBuffer.Length > MaxLogChars)
        {
            // Trim the oldest content, then snap to the next newline so the
            // remaining buffer always starts on a whole line.
            _logBuffer.Remove(0, _logBuffer.Length - MaxLogChars);
            for (var i = 0; i < 400 && i < _logBuffer.Length; i++)
            {
                if (_logBuffer[i] == '\n') { _logBuffer.Remove(0, i + 1); break; }
            }
        }

        LogText = _logBuffer.ToString();
    }

    // ─── Dialog helpers ────────────────────────────────────────────────────

    private Task<bool> ConfirmAsync(string body)
        => new ConfirmDialog(body).ShowDialog<bool>(_window);

    private Task ShowError(string title, string body)
        => new ConfirmDialog(body, title, errorMode: true).ShowDialog<bool>(_window);

    // ─── INotifyPropertyChanged ────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}