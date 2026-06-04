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
            OnPropertyChanged(nameof(IsNexusPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsLogPage));
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

    // ── Nexus Mods browser ───────────────────────────────────────────
    /// <summary>Cards rendered on the Nexus browser page (filtered view).</summary>
    public ObservableCollection<NexusModCard> NexusMods { get; } = new();

    /// <summary>Full pool of cards fetched from the API; NexusMods is
    /// derived from this via the current search query.</summary>
    private readonly List<NexusModCard> _allNexusMods = new();

    /// <summary>Toolbar search box on the Nexus page. Filters the loaded
    /// list client-side by name / author / summary substring match.</summary>
    private string _nexusSearchText = "";
    public  string  NexusSearchText
    {
        get => _nexusSearchText;
        set { if (Set(ref _nexusSearchText, value)) ApplyNexusSearch(); }
    }

    private void ApplyNexusSearch()
    {
        var q = (_nexusSearchText ?? "").Trim();
        NexusMods.Clear();
        if (q.Length == 0)
        {
            foreach (var m in _allNexusMods) NexusMods.Add(m);
        }
        else
        {
            foreach (var m in _allNexusMods)
            {
                if ((m.Name    ?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author  ?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Summary ?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    NexusMods.Add(m);
                }
            }
        }
    }

    /// <summary>
    /// Currently-detail-viewed mod. Null = the user is on the grid;
    /// non-null = drill-down on a single mod's image + summary + actions.
    /// </summary>
    private NexusModCard? _nexusSelectedMod;
    public  NexusModCard? NexusSelectedMod
    {
        get => _nexusSelectedMod;
        set
        {
            if (Set(ref _nexusSelectedMod, value))
            {
                OnPropertyChanged(nameof(IsNexusDetailVisible));
                OnPropertyChanged(nameof(IsNexusListVisible));
            }
        }
    }
    public bool IsNexusDetailVisible => _nexusSelectedMod is not null;
    public bool IsNexusListVisible   => _nexusSelectedMod is null && NexusContentReady;

    private void OpenSelectedNexusInBrowser()
    {
        var url = _nexusSelectedMod?.WebUrl;
        if (string.IsNullOrEmpty(url)) return;
        OpenInBrowser(url);
    }

    /// <summary>
    /// Sends the user to the mod's Files tab — that's where the green
    /// "Mod Manager Download" button lives. Clicking it on Nexus fires
    /// an nxm:// URL which our registered handler routes back to this
    /// app, where it lands in <see cref="HandleNxmUrlAsync"/>.
    /// </summary>
    private void OpenSelectedNexusFilesPage()
    {
        var url = _nexusSelectedMod?.WebUrl;
        if (string.IsNullOrEmpty(url)) return;
        OpenInBrowser(url + "?tab=files");
    }

    /// <summary>
    /// Drive the Nexus SSO flow end-to-end. On success the returned
    /// API key is written into the Settings field; the user still
    /// needs to click Save to persist it (so they have a chance to
    /// review and cancel if anything looks off). On failure the
    /// activity log gets a one-liner and the page surfaces an error.
    /// </summary>
    private async Task SignInWithNexusAsync()
    {
        if (_isSsoInFlight) return;
        _isSsoInFlight = true;
        try
        {
            AppendLog("Nexus SSO: opening approval page in your browser...");
            var key = await NexusSso.AuthenticateAsync(OpenInBrowser);
            NexusApiKeyField = key;
            AppendLog("Nexus SSO: received API key. Click Save to persist it.");
        }
        catch (Exception ex)
        {
            AppendLog($"Nexus SSO failed: {ex.Message}");
            await ShowError("Sign-in failed",
                $"{ex.Message}\n\nYou can still paste a Personal API key manually from " +
                "nexusmods.com → My Account → API.");
        }
        finally { _isSsoInFlight = false; }
    }
    private bool _isSsoInFlight;

    private static void OpenInBrowser(string url)
    {
        try
        {
            // Cross-platform "open URL in default browser": UseShellExecute
            // delegates to xdg-open on Linux, open on macOS, ShellExecute
            // on Windows.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    /// <summary>Set true when there's no API key configured yet — drives
    /// the "paste your key in Settings" empty-state panel.</summary>
    public bool NexusNeedsApiKey => string.IsNullOrWhiteSpace(_manager.State.NexusApiKey);

    /// <summary>Bound by the Settings page TextBox.</summary>
    private string _nexusApiKeyField = "";
    public  string  NexusApiKeyField
    {
        get => _nexusApiKeyField;
        set => Set(ref _nexusApiKeyField, value);
    }

    // Loading / error UI state for the Nexus page.
    private bool _isNexusLoading;
    public  bool  IsNexusLoading
    {
        get => _isNexusLoading;
        private set { if (Set(ref _isNexusLoading, value)) { OnPropertyChanged(nameof(NexusContentReady)); OnPropertyChanged(nameof(IsNexusListVisible)); } }
    }
    private string _nexusError = "";
    public  string  NexusError
    {
        get => _nexusError;
        private set { if (Set(ref _nexusError, value)) { OnPropertyChanged(nameof(HasNexusError)); OnPropertyChanged(nameof(NexusContentReady)); OnPropertyChanged(nameof(IsNexusListVisible)); } }
    }
    public bool HasNexusError => !string.IsNullOrEmpty(_nexusError);
    /// <summary>True only when we have cards to show AND no error/loading state up.</summary>
    public bool NexusContentReady => !IsNexusLoading && !HasNexusError;

    /// <summary>Cached game-categories map (id → name); set once per session.</summary>
    private Dictionary<int, string>? _nexusCategories;

    /// <summary>Which Nexus endpoint the card grid pulls from.</summary>
    public enum NexusFilter { Trending, LatestAdded, LatestUpdated }
    private NexusFilter _currentNexusFilter = NexusFilter.Trending;
    public  NexusFilter  CurrentNexusFilter
    {
        get => _currentNexusFilter;
        private set
        {
            if (Set(ref _currentNexusFilter, value))
            {
                OnPropertyChanged(nameof(IsTrendingFilter));
                OnPropertyChanged(nameof(IsLatestAddedFilter));
                OnPropertyChanged(nameof(IsLatestUpdatedFilter));
            }
        }
    }
    public bool IsTrendingFilter      => _currentNexusFilter == NexusFilter.Trending;
    public bool IsLatestAddedFilter   => _currentNexusFilter == NexusFilter.LatestAdded;
    public bool IsLatestUpdatedFilter => _currentNexusFilter == NexusFilter.LatestUpdated;

    /// <summary>
    /// Called when the user navigates to the Nexus page. If cards are
    /// already loaded we no-op (so flipping tabs doesn't re-hit the API).
    /// Otherwise spins up the active-filter fetch in the background.
    /// </summary>
    private void EnsureNexusSeeded()
    {
        if (NexusMods.Count > 0) return;
        if (string.IsNullOrWhiteSpace(_manager.State.NexusApiKey)) return;
        _ = LoadNexusAsync();
    }

    public async Task LoadNexusAsync()
    {
        var key = _manager.State.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        if (IsNexusLoading) return;

        IsNexusLoading = true;
        NexusError     = "";
        try
        {
            var client = new NexusClient(key);

            // Categories first (cached process-wide). Failure here is
            // non-fatal — cards just won't show category chips.
            _nexusCategories ??= await client.GetCategoryMapAsync();

            // Pick the endpoint matching the active filter pill. We
            // fetch the active filter first, then union in the other
            // two so the visible pool is closer to 25-30 unique mods
            // instead of the API's hard 10-per-list cap. The active
            // filter's mods land first in the list to preserve "this
            // is what Trending looks like" semantics.
            var primary = _currentNexusFilter switch
            {
                NexusFilter.LatestAdded   => await client.GetLatestAddedAsync(),
                NexusFilter.LatestUpdated => await client.GetLatestUpdatedAsync(),
                _                         => await client.GetTrendingAsync(),
            };
            List<NexusModListing> secondary, tertiary;
            try { secondary = await client.GetLatestAddedAsync();   } catch { secondary = new(); }
            try { tertiary  = await client.GetLatestUpdatedAsync(); } catch { tertiary  = new(); }
            var seen = new HashSet<int>();
            var list = new List<NexusModListing>();
            foreach (var m in primary.Concat(secondary).Concat(tertiary))
                if (seen.Add(m.ModId)) list.Add(m);

            // Filter out the empty stubs that occasionally appear in
            // the trending list — unpublished mods, deleted authors,
            // listings the API returns with blank fields. A mod with
            // no name, picture, or author isn't useful to browse.
            list = list.Where(m =>
                !string.IsNullOrWhiteSpace(m.Name) &&
                !string.IsNullOrWhiteSpace(m.PictureUrl) &&
                !string.IsNullOrWhiteSpace(m.Author)).ToList();

            _allNexusMods.Clear();
            foreach (var m in list)
            {
                var catName = (_nexusCategories.TryGetValue(m.CategoryId, out var n) ? n : "") ?? "";
                var card = new NexusModCard
                {
                    ModId        = m.ModId,
                    Name         = m.Name,
                    Author       = m.Author,
                    Category     = catName,
                    Summary      = m.Summary,
                    PictureUrl   = m.PictureUrl,
                    Endorsements = m.EndorsementCount,
                    Downloads    = m.Downloads,
                    Version      = m.Version,
                    WebUrl       = $"https://www.nexusmods.com/{m.DomainName}/mods/{m.ModId}",
                };
                _allNexusMods.Add(card);
                // Fire-and-forget thumbnail; updates the card's
                // Thumbnail property when done. UI swaps in via INPC.
                _ = LoadThumbnailAsync(card, client);
            }
            ApplyNexusSearch();
            OnPropertyChanged(nameof(NexusContentReady));
            OnPropertyChanged(nameof(IsNexusListVisible));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            NexusError = "API key was rejected. Re-check it in Settings.";
        }
        catch (TaskCanceledException)
        {
            NexusError = "Request timed out. Check your connection and try again.";
        }
        catch (Exception ex)
        {
            NexusError = $"Failed to load Nexus mods: {ex.Message}";
        }
        finally
        {
            IsNexusLoading = false;
        }
    }

    private static async Task LoadThumbnailAsync(NexusModCard card, NexusClient client)
    {
        if (string.IsNullOrEmpty(card.PictureUrl)) return;
        try
        {
            var bytes = await client.FetchImageBytesAsync(card.PictureUrl);
            using var ms = new MemoryStream(bytes);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            // Flip onto the UI thread for the INPC firing.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => card.Thumbnail = bmp);
        }
        catch
        {
            // Thumbnail fetch failures are silent — the card's
            // placeholder glyph already covers the empty case.
        }
    }

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
        // Accept both bare .mgsv files (what SnakeBite produces) AND
        // the wrapper archives that Nexus mods often ship in. The
        // archive types match what SharpCompress can crack open in
        // NexusDownloader: zip / rar / 7z / tar(.gz).
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Select .mgsv mod(s) or archive(s)",
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Mods + archives")
                {
                    Patterns = new[] { "*.mgsv", "*.zip", "*.rar", "*.7z", "*.tar", "*.tar.gz" },
                },
                new FilePickerFileType("SnakeBite mods") { Patterns = new[] { "*.mgsv" } },
                new FilePickerFileType("Archives")
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
            await ShowError("No mods to add",
                "None of the selected files contained an installable .mgsv mod.");
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
                summary += $"\n• and {failures.Count - 8} more failure(s)";
            await ShowError(
                $"{failures.Count} of {paths.Count} could not be added",
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

    /// <summary>Best-effort cleanup; failures are ignored.</summary>
    private static void CleanupTempDirs(IEnumerable<string> dirs)
    {
        foreach (var d in dirs)
        {
            try { System.IO.Directory.Delete(d, recursive: true); } catch { }
        }
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
            await Task.Run(() => _manager.RemoveMod(id));
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
            Title = "Select MGSV:TPP game root",
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
            Title          = "Select datfpk binary",
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("datfpk")     { Patterns = new[] { "datfpk", "datfpk.exe" } },
                new FilePickerFileType("Executables"){ Patterns = new[] { "*.exe", "*" } },
            },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null) DatFpkField = path;
    }

    private async Task ResetApplyStateAsync()
    {
        var ok = await ConfirmAsync(
            "Clear the Apply cache and tmp\\host_* scratch directories?\n\n" +
            "The next Apply will rebuild every host from scratch. This does NOT " +
            "touch your game install or the registered mod list.");
        if (!ok) return;
        try
        {
            await Task.Run(() => _manager.ResetApplyState());
        }
        catch (Exception ex) { await ShowError("Reset failed", ex.Message); }
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
                _nexusCategories = null;
                NexusError       = "";
            }
            OnPropertyChanged(nameof(GameRoot));
            OnPropertyChanged(nameof(DatFpk));
            OnPropertyChanged(nameof(NexusNeedsApiKey));
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
