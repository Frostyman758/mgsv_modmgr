using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using MgsvModMgr.Core;
using MgsvModMgr.Gui.Lang;

namespace MgsvModMgr.Gui;

// MainViewModel — Nexus Mods browser subsystem.
// Search / filters / pagination / detail fetch / SSO sign-in.
// Extracted into a partial sibling file to keep MainViewModel.cs
// under 500 lines.
public sealed partial class MainViewModel
{
    // ── Nexus Mods browser ───────────────────────────────────────────
    /// <summary>Cards rendered on the Nexus browser page (filtered view).</summary>
    public ObservableCollection<NexusModCard> NexusMods { get; } = new();

    /// <summary>Full pool of cards fetched from the API; NexusMods is
    /// derived from this via the current search query.</summary>
    private readonly List<NexusModCard> _allNexusMods = new();

    /// <summary>
    /// Toolbar search box on the Nexus page. Drives a real GraphQL
    /// wildcard query against the full Nexus catalog (not a client-
    /// side filter against the loaded set). Typing triggers a brief
    /// debounce — we wait ~350 ms after the last keystroke before
    /// issuing the API call so a quick "snake" doesn't fire four
    /// requests on the way.
    /// </summary>
    private string _nexusSearchText = "";
    public  string  NexusSearchText
    {
        get => _nexusSearchText;
        set { if (Set(ref _nexusSearchText, value)) ScheduleNexusSearchDebounce(); }
    }

    private Avalonia.Threading.DispatcherTimer? _nexusSearchDebounceTimer;
    private void ScheduleNexusSearchDebounce()
    {
        _nexusSearchDebounceTimer?.Stop();
        _nexusSearchDebounceTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _nexusSearchDebounceTimer.Tick += (_, _) =>
        {
            _nexusSearchDebounceTimer?.Stop();
            _nexusSearchDebounceTimer = null;
            // Only fire if we're on the Nexus page and have an API key —
            // otherwise this would needlessly thrash the server.
            if (!string.IsNullOrWhiteSpace(_manager.State.NexusApiKey))
                _ = LoadNexusAsync(append: false);
        };
        _nexusSearchDebounceTimer.Start();
    }

    /// <summary>Fires from the ScrollViewer at the end of the card grid.</summary>
    /// <summary>
    /// Drop every Nexus card + its decoded thumbnail. Called from the
    /// CurrentPage setter on exit. The bitmaps are the big-ticket
    /// memory consumers — each Nexus thumbnail decodes to a few MB,
    /// and a session that paginated 150 mods deep keeps half a gig
    /// pinned otherwise. Also resets the search debounce + pagination
    /// state so the next nav back to Nexus starts clean.
    /// </summary>
    private void ClearNexusCache()
    {
        foreach (var card in _allNexusMods)
        {
            try { card.Thumbnail?.Dispose(); } catch { }
            card.Thumbnail = null;
        }
        _allNexusMods.Clear();
        NexusMods.Clear();
        _nexusTotalAvailable = 0;
        _nexusSearchDebounceTimer?.Stop();
        _nexusSearchDebounceTimer = null;
        NexusSelectedMod = null;
        OnPropertyChanged(nameof(CanLoadMoreNexus));
        OnPropertyChanged(nameof(NexusContentReady));
        OnPropertyChanged(nameof(IsNexusListVisible));
    }

    public async Task LoadMoreNexusAsync()
    {
        if (CanLoadMoreNexus) await LoadNexusAsync(append: true);
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
                // Lazily fetch the full mod (description / tags /
                // requirements) the first time the user drills in.
                if (value is not null && !value.DetailsLoaded)
                    _ = LoadNexusModDetailsAsync(value);
            }
        }
    }

    private async Task LoadNexusModDetailsAsync(NexusModCard card)
    {
        var key = _manager.State.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            // Bail if we somehow don't have the gameId — better to
            // show the summary-only card than to fetch the wrong mod
            // by guessing a game id.
            if (card.GameId <= 0)
            {
                AppendLog($"Nexus: skipping detail fetch for mod {card.ModId} — no gameId.");
                return;
            }
            var gql = new NexusGraphQL(key);
            // Pass the gameId that came back from the list query for
            // this exact card. Hardcoding it was the source of the
            // "wrong description" bug.
            var detail = await gql.GetModAsync(card.ModId, gameId: card.GameId);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Per the user — only the About summary is shown on
                // the detail view now. We still fetch tags +
                // requirements (small, cheap), but the description
                // body is intentionally ignored to keep the page
                // light and the wrong-mod-data confusion gone.
                card.Tags.Clear();
                foreach (var t in detail.Tags) card.Tags.Add(t);
                card.Requirements.Clear();
                foreach (var r in detail.Requirements)
                    card.Requirements.Add(new NexusRequirementRow
                    {
                        ModName             = r.ModName,
                        Notes               = r.Notes,
                        Url                 = r.Url,
                        ExternalRequirement = r.ExternalRequirement,
                    });
                card.DetailsLoaded = true;
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Nexus: detail fetch failed for mod {card.ModId}: {ex.Message}");
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
    /// <summary>
    /// Click on the "by <author>" line in the detail view → return to
    /// the browse grid filtered to that author's mods.
    /// </summary>
    private void FilterByCurrentAuthor()
    {
        var who = _nexusSelectedMod?.Author;
        if (string.IsNullOrWhiteSpace(who)) return;
        NexusAuthorFilter   = who;
        NexusCategoryFilter = null;
        NexusTagFilter      = null;
        NexusSelectedMod    = null;       // back to grid
        _ = LoadNexusAsync(append: false);
    }

    /// <summary>
    /// Click on the category chip in the detail view → return to the
    /// browse grid filtered to that category.
    /// </summary>
    private void FilterByCurrentCategory()
    {
        var cat = _nexusSelectedMod?.Category;
        if (string.IsNullOrWhiteSpace(cat)) return;
        NexusAuthorFilter   = null;
        NexusCategoryFilter = cat;
        NexusTagFilter      = null;
        NexusSelectedMod    = null;
        _ = LoadNexusAsync(append: false);
    }

    /// <summary>Public so the tag-chip data template can bind to it.</summary>
    public void FilterByTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        NexusAuthorFilter   = null;
        NexusCategoryFilter = null;
        NexusTagFilter      = tag;
        NexusSelectedMod    = null;
        _ = LoadNexusAsync(append: false);
    }

    /// <summary>Bound by the "× clear filter" chip above the card grid.</summary>
    private void ClearFacetFilters()
    {
        if (!HasAnyFacetFilter) return;
        NexusAuthorFilter   = null;
        NexusCategoryFilter = null;
        NexusTagFilter      = null;
        _ = LoadNexusAsync(append: false);
    }

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
            await ShowError(L.S("Str.Errors.SignInFailed"),
                ex.Message + L.S("Str.Errors.SignInFailedSuffix"));
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

    /// <summary>
    /// Light-vs-dark theme preference. Persisted in state.txt as
    /// `theme=light` / `theme=dark`. Writes through to
    /// Application.Current.RequestedThemeVariant immediately so the
    /// switch happens live, with all DynamicResource brush bindings
    /// crossfading via Avalonia's theme-variant resource lookup.
    /// </summary>
    public bool IsLightTheme
    {
        get => _manager.State.IsLightTheme;
        set
        {
            if (_manager.State.IsLightTheme == value) return;
            _manager.State.IsLightTheme = value;
            ApplyThemeVariant();
            _manager.SaveState();
            OnPropertyChanged();
        }
    }

    private void ApplyThemeVariant()
    {
        var app = Avalonia.Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = _manager.State.IsLightTheme
            ? Avalonia.Styling.ThemeVariant.Light
            : Avalonia.Styling.ThemeVariant.Dark;
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

    // Click-routed facet filters from the detail view. When non-null,
    // the grid loads only mods matching that author / category / tag.
    private string? _nexusAuthorFilter;
    public  string? NexusAuthorFilter
    {
        get => _nexusAuthorFilter;
        private set { if (Set(ref _nexusAuthorFilter, value)) { OnPropertyChanged(nameof(HasAnyFacetFilter)); OnPropertyChanged(nameof(FacetFilterDisplay)); } }
    }
    private string? _nexusCategoryFilter;
    public  string? NexusCategoryFilter
    {
        get => _nexusCategoryFilter;
        private set { if (Set(ref _nexusCategoryFilter, value)) { OnPropertyChanged(nameof(HasAnyFacetFilter)); OnPropertyChanged(nameof(FacetFilterDisplay)); } }
    }
    private string? _nexusTagFilter;
    public  string? NexusTagFilter
    {
        get => _nexusTagFilter;
        private set { if (Set(ref _nexusTagFilter, value)) { OnPropertyChanged(nameof(HasAnyFacetFilter)); OnPropertyChanged(nameof(FacetFilterDisplay)); } }
    }
    public bool HasAnyFacetFilter =>
        !string.IsNullOrEmpty(_nexusAuthorFilter)
        || !string.IsNullOrEmpty(_nexusCategoryFilter)
        || !string.IsNullOrEmpty(_nexusTagFilter);
    public string FacetFilterDisplay
    {
        get
        {
            if (!string.IsNullOrEmpty(_nexusAuthorFilter))   return $"by {_nexusAuthorFilter}";
            if (!string.IsNullOrEmpty(_nexusCategoryFilter)) return $"category: {_nexusCategoryFilter}";
            if (!string.IsNullOrEmpty(_nexusTagFilter))      return $"tag: {_nexusTagFilter}";
            return "";
        }
    }

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

    /// <summary>Page-size for each GraphQL <c>mods</c> request.</summary>
    private const int NexusPageSize = 24;

    /// <summary>
    /// Total mod count the server reports for the current filter +
    /// search. Used to gate the "load more" call so we stop hitting
    /// the API once we've fetched everything.
    /// </summary>
    private int _nexusTotalAvailable;
    public bool CanLoadMoreNexus => !IsNexusLoading
                                 && _allNexusMods.Count < _nexusTotalAvailable;

    public async Task LoadNexusAsync(bool append = false)
    {
        var key = _manager.State.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        if (IsNexusLoading) return;

        IsNexusLoading = true;
        NexusError     = "";
        try
        {
            var gql = new NexusGraphQL(key);

            var sort = _currentNexusFilter switch
            {
                NexusFilter.LatestAdded   => NexusGraphQL.Sort.LatestAdded,
                NexusFilter.LatestUpdated => NexusGraphQL.Sort.LatestUpdated,
                _                         => NexusGraphQL.Sort.Trending,
            };

            // Search text (if any) is now a real backend wildcard
            // match against the mod name — no more client-side
            // substring filter pretending to be search.
            var search = string.IsNullOrWhiteSpace(_nexusSearchText) ? null : _nexusSearchText;
            var offset = append ? _allNexusMods.Count : 0;

            var page = await gql.ListModsAsync(
                gameDomainName: NexusClient.GameDomain,
                sort:           sort,
                offset:         offset,
                count:          NexusPageSize,
                search:         search,
                author:         _nexusAuthorFilter,
                category:       _nexusCategoryFilter,
                tag:            _nexusTagFilter);

            // Drop the empty stubs (deleted/unpublished mods occasionally
            // surface in the listing with blank fields).
            var filtered = page.Mods
                .Select((m, i) => (Mod: m, Category: i < page.Categories.Count ? page.Categories[i] : ""))
                .Where(p => !string.IsNullOrWhiteSpace(p.Mod.Name)
                         && !string.IsNullOrWhiteSpace(p.Mod.PictureUrl)
                         && !string.IsNullOrWhiteSpace(p.Mod.Author))
                .ToList();

            // Initial load = reset; "load more" = append.
            if (!append)
            {
                _allNexusMods.Clear();
                NexusMods.Clear();
            }

            var imageClient = new NexusClient(key);
            foreach (var (m, catName) in filtered)
            {
                var domain = string.IsNullOrEmpty(m.DomainName) ? NexusClient.GameDomain : m.DomainName;
                var card = new NexusModCard
                {
                    ModId        = m.ModId,
                    GameId       = m.GameId,
                    Name         = m.Name,
                    Author       = m.Author,
                    Category     = catName,
                    Summary      = m.Summary,
                    PictureUrl   = m.PictureUrl,
                    Endorsements = m.EndorsementCount,
                    Downloads    = m.Downloads,
                    Version      = m.Version,
                    WebUrl       = $"https://www.nexusmods.com/{domain}/mods/{m.ModId}",
                };
                _allNexusMods.Add(card);
                NexusMods.Add(card);
                _ = LoadThumbnailAsync(card, imageClient);
            }

            _nexusTotalAvailable = page.TotalCount;

            OnPropertyChanged(nameof(NexusContentReady));
            OnPropertyChanged(nameof(IsNexusListVisible));
            OnPropertyChanged(nameof(CanLoadMoreNexus));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            NexusError = L.S("Str.Nexus.Err.ApiKey");
        }
        catch (TaskCanceledException)
        {
            NexusError = L.S("Str.Nexus.Err.Timeout");
        }
        catch (Exception ex)
        {
            NexusError = L.F("Str.Nexus.Err.LoadFmt", ex.Message);
        }
        finally
        {
            IsNexusLoading = false;
            OnPropertyChanged(nameof(CanLoadMoreNexus));
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
}