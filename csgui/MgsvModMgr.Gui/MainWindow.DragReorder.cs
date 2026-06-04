using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MgsvModMgr.Gui;

// MainWindow — lift-and-drag row reordering for the Mods page.
// Picks up a row on PointerPressed, renders a translucent ghost
// that tracks the cursor, draws an insertion indicator between
// rows, commits the move on Release. Auto-scrolls the DataGrid
// when the cursor nears the top/bottom edge. Extracted into a
// partial sibling to keep MainWindow.axaml.cs under 500 lines.
public partial class MainWindow
{
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

}