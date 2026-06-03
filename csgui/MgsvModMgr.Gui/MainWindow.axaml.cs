using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MgsvModMgr.Gui;

public partial class MainWindow : Window
{
    // Endpoints for the live "dirty" colour ramp.
    //   mix = 1.0 → amber (full pending / fresh-dirty, no Apply running)
    //   mix = 0.0 → red   (clean, Apply finished)
    // During Apply, MainViewModel.DirtyMix glides 1 → 0 in lockstep with
    // ApplyProgress, so the brush — and every control bound through it —
    // shifts colour live as the install bar fills.
    private static readonly Color DirtyAmber = Color.FromRgb(0xB8, 0x90, 0x30);
    private static readonly Color DirtyRed   = Color.FromRgb(0xB6, 0x2A, 0x2A);

    private SolidColorBrush? _dirtyBrush;

    public MainWindow()
    {
        InitializeComponent();
        // DevTools always on so the user can press F12 in the shipped
        // build, drill into any visual, and live-edit Margin / Padding /
        // brushes in the property grid. Avalonia.Diagnostics is already
        // referenced; this just wires up the F12 binding.
        this.AttachDevTools();
        var vm = new MainViewModel(this);
        DataContext = vm;
        Opened += async (_, _) =>
        {
            await vm.OnOpenedAsync();
            // Apply persisted column-visibility prefs from state.txt
            // now that the DataGrid template is realised and the VM
            // has its ModManager loaded.
            ApplyHiddenColumnsFromState();
        };

        AttachGridHooks();
        HookDirtyBrush(vm);

        // Right-click anywhere on the page header (blank area next to
        // "Installed mods" / between the title and the toolbar) → pops
        // the column-visibility menu. NAME and ENABLED are filtered
        // out as essentials.
        if (ModsHeader is not null)
            ModsHeader.AddHandler(
                ContextRequestedEvent,
                ModsHeader_ContextRequested,
                Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Wire MainViewModel.DirtyMix to the shared DirtyBrush resource. We
    /// mutate the same SolidColorBrush instance's Color property on every
    /// change rather than swapping the resource — Avalonia brushes notify
    /// their consumers when Color updates, so every control that bound
    /// {DynamicResource DirtyBrush} repaints in the same frame. This is
    /// what makes the install bar, toggle tracks, Add button and sidebar
    /// Apply pip all shift colour live as ApplyProgress moves.
    /// </summary>
    private void HookDirtyBrush(MainViewModel vm)
    {
        if (Application.Current?.Resources.TryGetValue("DirtyBrush", out var res) == true
            && res is SolidColorBrush brush)
        {
            _dirtyBrush = brush;
        }
        vm.PropertyChanged += OnVmPropertyChanged;
        UpdateDirtyBrush(vm.DirtyMix);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DirtyMix)
            && DataContext is MainViewModel vm)
            UpdateDirtyBrush(vm.DirtyMix);
    }

    private void UpdateDirtyBrush(double mix)
    {
        if (_dirtyBrush is null) return;
        mix = Math.Clamp(mix, 0.0, 1.0);
        // Linear RGB lerp between red (mix=0) and amber (mix=1).
        var r = (byte)Math.Round(DirtyRed.R + (DirtyAmber.R - DirtyRed.R) * mix);
        var g = (byte)Math.Round(DirtyRed.G + (DirtyAmber.G - DirtyRed.G) * mix);
        var b = (byte)Math.Round(DirtyRed.B + (DirtyAmber.B - DirtyRed.B) * mix);
        _dirtyBrush.Color = Color.FromRgb(r, g, b);
    }

    private void AttachGridHooks()
    {
        if (ModListGrid is null) return;
        ModListGrid.AddHandler(PointerPressedEvent,  Grid_PointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ModListGrid.AddHandler(PointerMovedEvent,    Grid_PointerMoved);
        ModListGrid.AddHandler(PointerReleasedEvent, Grid_PointerReleased);
    }

    private const double DragThresholdPx = 5;
    private const double EdgeBandPx      = 90;
    private const double MaxScrollPxPer  = 28;

    private ModRow? _dragSource;
    private Point   _pressOrigin;
    private bool    _dragArmed;
    private bool    _dragging;
    private int     _dragTargetIndex = -1;
    // Latest cursor position in window-coords, refreshed on every
    // PointerMoved. The auto-scroll timer re-derives speed from this
    // each tick, so scrolling continues even when the cursor is held
    // dead-still at the edge of the viewport (PointerMoved stops
    // firing without motion, but the timer doesn't).
    private Point   _lastDragCursor;

    private DispatcherTimer? _autoScrollTimer;
    private double           _autoScrollSpeed;
    private ScrollViewer?    _gridScrollViewer;

    private ScrollViewer? FindGridScrollViewer()
    {
        if (ModListGrid is null) return null;
        // DataGrid's template has multiple ScrollViewers (column header
        // strip, frozen-column areas, body). Most of them have zero
        // vertical extent — the body is the one with a real scrollable
        // range. Don't cache: which SV "has scrollable content" can
        // change as the data set / window size change.
        ScrollViewer? best = null;
        double bestExtent = 0;
        foreach (var sv in ModListGrid.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var extentY = sv.Extent.Height;
            if (extentY > bestExtent)
            {
                bestExtent = extentY;
                best = sv;
            }
        }
        return best;
    }

    private static ModRow? RowUnder(Visual? hit)
    {
        for (var node = hit; node is not null; node = node.GetVisualParent())
        {
            if (node is DataGridRow row && row.DataContext is ModRow mr) return mr;
            if (node is Control ctrl && ctrl.DataContext is ModRow d)    return d;
        }
        return null;
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var row = RowUnder(e.Source as Visual);
        if (row is null) return;

        // Don't intercept clicks that landed on the toggle switch — it owns
        // its hit area and should always toggle, never start a drag.
        if (e.Source is Visual src)
        {
            for (var n = (Visual?)src; n is not null; n = n.GetVisualParent())
                if (n is ToggleSwitch) return;
        }

        _dragSource  = row;
        _pressOrigin = e.GetPosition(this);
        _dragArmed   = true;
        // Hold pointer focus on the grid so we keep receiving Moved/Released
        // even when the cursor leaves a particular DataGridRow's bounds.
        e.Pointer.Capture(ModListGrid);
    }

    /// <summary>
    /// Lift-and-drag. The dragged row stays put in the list visually; a
    /// floating "ghost" of it follows the cursor, and a horizontal
    /// insert-line snaps into the gap between two rows to show where
    /// the drop will land. The collection isn't touched until the user
    /// releases — so the user can travel freely across the whole list
    /// (and use auto-scroll near the edges) before committing.
    /// </summary>
    private void Grid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null) return;

        var p = e.GetPosition(this);

        // Arm → active drag transition: wait for the user to commit to a
        // real gesture (>5px) before showing the ghost; otherwise a plain
        // row click would briefly flash drag UI.
        if (_dragArmed)
        {
            if (Math.Abs(p.Y - _pressOrigin.Y) < DragThresholdPx &&
                Math.Abs(p.X - _pressOrigin.X) < DragThresholdPx) return;
            _dragArmed = false;
            _dragging  = true;
            StartGhost();
        }
        if (!_dragging) return;

        // Remember the cursor; auto-scroll timer re-uses it every tick.
        _lastDragCursor = p;

        // Auto-scroll while dragging near the viewport edges, so long
        // lists are navigable without releasing the mouse. The actual
        // scrolling work lives in AutoScrollTick — it recomputes speed
        // from the latest cursor position so it still scrolls when the
        // cursor is held stationary at the edge band.
        EnsureAutoScrollTimer();

        UpdateGhostPosition(p);
        UpdateInsertionIndicator(p);
    }

    private void Grid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopAutoScroll();
        e.Pointer.Capture(null);

        // Commit the move only on release — to the target slot the
        // insertion indicator was pointing at when the user let go.
        if (_dragging
            && _dragSource is not null
            && _dragTargetIndex >= 0
            && DataContext is MainViewModel vm)
        {
            var srcIdx = vm.Mods.IndexOf(_dragSource);
            // _dragTargetIndex is the *insert-before* slot; when source
            // sits above the target slot we need to compensate for the
            // removal that ObservableCollection.Move does internally.
            var dest = (srcIdx >= 0 && srcIdx < _dragTargetIndex)
                ? _dragTargetIndex - 1
                : _dragTargetIndex;
            if (srcIdx >= 0 && dest != srcIdx && dest >= 0 && dest < vm.Mods.Count)
                vm.MoveRowToIndex(_dragSource.Id, dest);
        }

        HideGhost();
        _dragArmed  = false;
        _dragging   = false;
        _dragSource = null;
        _dragTargetIndex = -1;
    }

    // ────────────────────────────────────────────────────────────────
    // Drag ghost + insertion indicator
    // ────────────────────────────────────────────────────────────────

    private void StartGhost()
    {
        if (_dragSource is null || DragGhost is null || DragGhostText is null) return;
        DragGhostText.Text = _dragSource.Name;
        DragGhost.IsVisible = true;
    }

    private void HideGhost()
    {
        if (DragGhost      is not null) DragGhost.IsVisible      = false;
        if (DragInsertLine is not null) DragInsertLine.IsVisible = false;
    }

    private void UpdateGhostPosition(Point cursor)
    {
        if (DragGhost is null) return;
        // Float the ghost slightly down-right of the cursor so the cursor
        // arrow stays visible and the ghost reads as "attached" to it.
        Canvas.SetLeft(DragGhost, cursor.X + 14);
        Canvas.SetTop (DragGhost, cursor.Y - 10);
    }

    /// <summary>
    /// Figure out which gap-between-rows the cursor is currently aimed at
    /// and paint the insert line there. Stores the resulting insert
    /// index in <see cref="_dragTargetIndex"/> for the release handler.
    /// Index semantics: "the slot to insert before" — i.e. 0 means top
    /// of list, vm.Mods.Count means bottom.
    /// </summary>
    private void UpdateInsertionIndicator(Point cursor)
    {
        if (DataContext is not MainViewModel vm || ModListGrid is null || DragInsertLine is null)
        {
            _dragTargetIndex = -1;
            return;
        }

        var rows = ModListGrid
            .GetVisualDescendants()
            .OfType<DataGridRow>()
            .Where(r => r.DataContext is ModRow)
            .OrderBy(r =>
            {
                var t = r.TransformToVisual(this);
                return t.HasValue ? t.Value.Transform(new Point(0, 0)).Y : double.MaxValue;
            })
            .ToList();
        if (rows.Count == 0) { _dragTargetIndex = -1; DragInsertLine.IsVisible = false; return; }

        int insertIndex = -1;
        double indicatorY = 0, indicatorLeft = 0, indicatorWidth = 0;

        foreach (var dgr in rows)
        {
            var t = dgr.TransformToVisual(this);
            if (!t.HasValue) continue;
            var tl = t.Value.Transform(new Point(0, 0));
            var midY = tl.Y + dgr.Bounds.Height / 2.0;
            if (cursor.Y < midY)
            {
                if (dgr.DataContext is ModRow mr)
                {
                    insertIndex   = vm.Mods.IndexOf(mr);
                    indicatorY    = tl.Y;
                    indicatorLeft = tl.X;
                    indicatorWidth = dgr.Bounds.Width;
                }
                break;
            }
        }
        if (insertIndex < 0)
        {
            // Cursor is below every row → insert at the end.
            var last = rows[^1];
            var t = last.TransformToVisual(this);
            if (t.HasValue)
            {
                var tl = t.Value.Transform(new Point(0, 0));
                insertIndex   = vm.Mods.Count;
                indicatorY    = tl.Y + last.Bounds.Height;
                indicatorLeft = tl.X;
                indicatorWidth = last.Bounds.Width;
            }
        }

        _dragTargetIndex = insertIndex;
        if (insertIndex >= 0)
        {
            Canvas.SetLeft(DragInsertLine, indicatorLeft);
            Canvas.SetTop (DragInsertLine, indicatorY - 1);
            DragInsertLine.Width = indicatorWidth;
            DragInsertLine.IsVisible = true;
        }
        else
        {
            DragInsertLine.IsVisible = false;
        }
    }

    private void EnsureAutoScrollTimer()
    {
        if (_autoScrollTimer is not null) return;
        _autoScrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            AutoScrollTick);
        _autoScrollTimer.Start();
    }

    /// <summary>
    /// Find the body ScrollViewer the DataGrid uses to scroll its rows.
    /// The DataGrid template nests several ScrollViewers (frozen-column
    /// areas, header strip, etc.); the one we want is the body — pick
    /// it by largest Viewport.Height, since the body is always taller
    /// than the header strip / corner gutters.
    /// </summary>
    private ScrollViewer? FindBodyScrollViewer()
    {
        if (ModListGrid is null) return null;
        ScrollViewer? best = null;
        double bestViewport = 0;
        foreach (var sv in ModListGrid.GetVisualDescendants().OfType<ScrollViewer>())
        {
            // Only consider SVs that actually have somewhere to scroll to.
            if (sv.ScrollBarMaximum.Y <= 0) continue;
            var vp = sv.Viewport.Height;
            if (vp > bestViewport)
            {
                bestViewport = vp;
                best = sv;
            }
        }
        return best;
    }

    private void AutoScrollTick(object? sender, EventArgs e)
    {
        if (!_dragging) return;
        if (ModListGrid is null) return;
        if (DataContext is not MainViewModel vm) return;
        if (vm.Mods.Count == 0) return;

        // Ghost-in-ModListGrid-local-coords. If it's not in the top or
        // bottom edge band, there's nothing to do this tick.
        var t = ModListGrid.TransformToVisual(this);
        if (!t.HasValue || !t.Value.TryInvert(out var inv)) return;
        var localY = inv.Transform(new Point(0, _lastDragCursor.Y - 10)).Y;
        var h = ModListGrid.Bounds.Height;

        int direction;
        double edgePenetration;
        if (localY < EdgeBandPx)
        {
            direction = -1;
            edgePenetration = 1.0 - Math.Max(0, localY) / EdgeBandPx;
        }
        else if (localY > h - EdgeBandPx)
        {
            direction = +1;
            edgePenetration = (localY - (h - EdgeBandPx)) / EdgeBandPx;
        }
        else return;
        edgePenetration = Math.Clamp(edgePenetration, 0.05, 1.5);

        // Smooth pixel scroll: mutate the body ScrollViewer's Offset
        // directly. Avalonia's DataGrid honours Offset writes the same
        // way ScrollIntoView does internally, but at sub-row pixel
        // granularity, so the rows glide instead of snap. Step is
        // proportional to how deep we are into the edge band.
        var sv = FindBodyScrollViewer();
        if (sv is not null)
        {
            double step = MaxScrollPxPer * edgePenetration;
            double max  = sv.ScrollBarMaximum.Y;
            double next = Math.Clamp(sv.Offset.Y + direction * step, 0, max);
            if (Math.Abs(next - sv.Offset.Y) > 0.01)
            {
                sv.Offset = new Vector(sv.Offset.X, next);
                UpdateInsertionIndicator(_lastDragCursor);
            }
            return;
        }

        // Fallback: if no ScrollViewer is realised yet, scroll by row
        // index via DataGrid.ScrollIntoView. Less smooth (snaps per row)
        // but always works regardless of template state.
        var visible = ModListGrid.GetVisualDescendants()
            .OfType<DataGridRow>()
            .Where(r => r.DataContext is ModRow)
            .Select(r =>
            {
                var tt = r.TransformToVisual(this);
                var y  = tt.HasValue ? tt.Value.Transform(new Point(0, 0)).Y : double.NaN;
                return (Row: r, Y: y);
            })
            .Where(p => !double.IsNaN(p.Y))
            .ToList();
        if (visible.Count == 0) return;

        DataGridRow anchor = direction < 0
            ? visible.OrderBy(p => p.Y).First().Row
            : visible.OrderByDescending(p => p.Y).First().Row;
        if (anchor.DataContext is not ModRow anchorMr) return;
        int anchorIdx = vm.Mods.IndexOf(anchorMr);
        if (anchorIdx < 0) return;

        int step2 = Math.Max(1, (int)Math.Round(edgePenetration * 2.0));
        int targetIdx = Math.Clamp(anchorIdx + direction * step2, 0, vm.Mods.Count - 1);
        if (targetIdx == anchorIdx) return;
        try { ModListGrid.ScrollIntoView(vm.Mods[targetIdx], null); } catch { }
        UpdateInsertionIndicator(_lastDragCursor);
    }

    private void StopAutoScroll()
    {
        _autoScrollSpeed = 0;
        _autoScrollTimer?.Stop();
        _autoScrollTimer = null;
    }

    private void UpdateAutoScrollFor(Point cursorInScroll, double scrollHeight)
    {
        if (cursorInScroll.Y < EdgeBandPx)
            _autoScrollSpeed = -MaxScrollPxPer * (1 - cursorInScroll.Y / EdgeBandPx);
        else if (cursorInScroll.Y > scrollHeight - EdgeBandPx)
            _autoScrollSpeed = MaxScrollPxPer * ((cursorInScroll.Y - (scrollHeight - EdgeBandPx)) / EdgeBandPx);
        else
            _autoScrollSpeed = 0;
    }

    private void Footer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.HideFooter();
    }

    // ────────────────────────────────────────────────────────────────
    // Search pill: collapses ↔ expands via the Border's Width transition.
    // ────────────────────────────────────────────────────────────────

    private const double SearchExpandedWidth = 280;

    private void SearchPill_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only respond when collapsed — once expanded, clicks inside go
        // to the TextBox and should not re-trigger expansion logic.
        if (SearchBox is null || SearchBox.IsVisible) return;
        ExpandSearch();
        e.Handled = true;
    }

    private void ExpandSearch()
    {
        if (SearchPill is null || SearchBox is null) return;
        SearchBox.IsVisible = true;
        SearchPill.Width    = SearchExpandedWidth;
        // Toggle the .expanded pseudo-class on the outer pill; the
        // Theme.axaml styles take it from there (red backplate + white
        // glyph) — keeps brush ownership inside XAML where DirtyBrush
        // live-shifts cleanly without code-behind interpolation.
        if (!SearchPill.Classes.Contains("expanded"))
            SearchPill.Classes.Add("expanded");
        // Focus on the next dispatcher tick so the visibility flip has
        // propagated through layout before the focus call lands.
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
    }

    private void CollapseSearch()
    {
        if (SearchPill is null || SearchBox is null) return;
        SearchBox.IsVisible = false;
        SearchPill.Width    = 40;
        SearchPill.Classes.Remove("expanded");
    }

    private void SearchBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Auto-collapse only if the user didn't actually search anything.
        // If they typed a query, keep the bar open so the filter stays
        // visible/visible-feedback even when focus moves elsewhere.
        if (string.IsNullOrEmpty(SearchBox?.Text)) CollapseSearch();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox is not null) SearchBox.Text = "";
            CollapseSearch();
            e.Handled = true;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Column visibility: right-click any column header for a toggle menu
    // ────────────────────────────────────────────────────────────────

    private static readonly string[] EssentialColumns = { "NAME", "ENABLED" };

    /// <summary>
    /// Walks each DataGrid column once at startup and flips IsVisible
    /// off for any column header that's listed in MainViewModel
    /// HiddenColumns (read from state.txt). Essentials are never
    /// touched even if a stale state file lists them.
    /// </summary>
    private void ApplyHiddenColumnsFromState()
    {
        if (ModListGrid is null) return;
        if (DataContext is not MainViewModel vm) return;
        foreach (var col in ModListGrid.Columns)
        {
            var header = col.Header?.ToString() ?? "";
            if (Array.IndexOf(EssentialColumns, header) >= 0) continue;
            col.IsVisible = !vm.IsColumnHidden(header);
        }
    }

    private void ModsHeader_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        try
        {
            if (ModListGrid is null || ModsHeader is null) return;

            // Ignore right-clicks that landed on the toolbar buttons
            // (Up/Down/Search/Trash/Add) — those should keep their own
            // default behaviour. Anything else inside the header strip
            // (title text, blank space) pops the column menu.
            if (e.Source is Visual src)
            {
                for (var n = src; n is not null && !ReferenceEquals(n, ModsHeader); n = n.GetVisualParent())
                {
                    if (n is Button) return;
                }
            }

            var flyout = new MenuFlyout
            {
                // Pops up exactly where the user right-clicked, not
                // pinned to the header strip's bottom edge.
                Placement = PlacementMode.Pointer,
            };
            foreach (var col in ModListGrid.Columns)
            {
                var header = col.Header?.ToString() ?? "";
                if (Array.IndexOf(EssentialColumns, header) >= 0) continue;

                var item = new MenuItem
                {
                    Header     = header,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked  = col.IsVisible,
                };
                var capturedCol  = col;
                var capturedItem = item;
                var capturedName = header;
                item.Click += (_, _) =>
                {
                    capturedCol.IsVisible  = !capturedCol.IsVisible;
                    capturedItem.IsChecked = capturedCol.IsVisible;
                    // Persist: hidden=true → write hidecol=NAME to
                    // state.txt; visible=true → remove that line.
                    if (DataContext is MainViewModel mvm)
                        mvm.SetColumnHidden(capturedName, !capturedCol.IsVisible);
                };
                flyout.Items.Add(item);
            }

            flyout.ShowAt(ModsHeader);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            // Never let a right-click bring the app down.
            System.Diagnostics.Debug.WriteLine($"[header menu] {ex}");
        }
    }

    /// <summary>
    /// Live "jump-to" search: on every keystroke, find the first mod
    /// whose Name or Id contains the query (case-insensitive), select
    /// it, and scroll the DataGrid until it's visible. No filtering —
    /// the list stays intact, the user just gets fast navigation.
    /// </summary>
    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (ModListGrid is null) return;
        var q = SearchBox?.Text;
        if (string.IsNullOrWhiteSpace(q)) return;

        var match = vm.Mods.FirstOrDefault(m =>
            (m.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (m.Id?.Contains(q, StringComparison.OrdinalIgnoreCase)   ?? false));
        if (match is null) return;

        vm.SelectedMod = match;
        try { ModListGrid.ScrollIntoView(match, null); } catch { }
    }
}
