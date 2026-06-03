using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MgsvModMgr.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        var vm = new MainViewModel(this);
        DataContext = vm;
        Opened += async (_, _) => await vm.OnOpenedAsync();

        // Drop-target wiring at the window level so any row's DragOver/Drop
        // events bubble here. We resolve the target row via VisualTreeHelper
        // inside the handler instead of attaching per-Border drop handlers
        // (which would multiply the wiring).
        AddHandler(DragDrop.DragOverEvent, Row_DragOver);
        AddHandler(DragDrop.DropEvent,     Row_Drop);
    }

    // ── Drag-to-reorder for mod rows ─────────────────────────────────────
    //
    // PointerPressed records the would-be source row + cursor origin. We
    // don't kick the DragDrop session until PointerMoved has crossed a
    // small threshold (>=5px), so a plain click still selects the row
    // without spuriously starting a drag.
    //
    // The DataObject carries just the mod id as a string; on drop the
    // handler looks up the *target row* under the cursor and calls
    // MainViewModel.MoveRowToIndex to commit. State.Mods and the cache
    // both fingerprint by mod id, so a move invalidates the right hosts
    // automatically.

    private const double DragThresholdPx = 5;
    private ModRow? _dragSource;
    private Point _pressOrigin;
    private bool _dragArmed;

    // Auto-scroll the mod list when the drag cursor is near its top or
    // bottom edge — otherwise long mod lists are impossible to reorder
    // across the viewport without releasing, scrolling by hand, picking
    // up again. The timer ticks at ~60 Hz; the speed is recomputed in
    // DragOver based on how deep the cursor is into the edge band.
    private const double EdgeBandPx     = 60;
    private const double MaxScrollPxPer = 12;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollSpeed;

    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not ModRow row) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _dragSource  = row;
        _pressOrigin = e.GetPosition(this);
        _dragArmed   = true;
    }

    private async void Row_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || _dragSource is null) return;
        var p  = e.GetPosition(this);
        var dy = Math.Abs(p.Y - _pressOrigin.Y);
        var dx = Math.Abs(p.X - _pressOrigin.X);
        if (dy < DragThresholdPx && dx < DragThresholdPx) return;

        _dragArmed = false;

        var data = new DataObject();
        data.Set("modmgr.row.id", _dragSource.Id);
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        catch { /* swallow Avalonia's "no active drag" if drop didn't land */ }
        finally
        {
            StopAutoScroll();
            _dragSource = null;
        }
    }

    private void Row_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopAutoScroll();
        _dragArmed  = false;
        _dragSource = null;
    }

    // ── Auto-scroll while dragging near the list's edges ────────────────

    private void EnsureAutoScrollTimer()
    {
        if (_autoScrollTimer is not null) return;
        _autoScrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Background,
            AutoScrollTick);
        _autoScrollTimer.Start();
    }

    private void AutoScrollTick(object? sender, EventArgs e)
    {
        if (Math.Abs(_autoScrollSpeed) < 0.01) return;
        if (ModListScroll is null) return;
        var max = ModListScroll.ScrollBarMaximum;
        var off = ModListScroll.Offset;
        var nextY = Math.Clamp(off.Y + _autoScrollSpeed, 0, max.Y);
        if (nextY != off.Y) ModListScroll.Offset = new Vector(off.X, nextY);
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
        {
            var depth = 1 - (cursorInScroll.Y / EdgeBandPx);
            _autoScrollSpeed = -MaxScrollPxPer * depth;
        }
        else if (cursorInScroll.Y > scrollHeight - EdgeBandPx)
        {
            var depth = (cursorInScroll.Y - (scrollHeight - EdgeBandPx)) / EdgeBandPx;
            _autoScrollSpeed = MaxScrollPxPer * depth;
        }
        else
        {
            _autoScrollSpeed = 0;
        }
    }

    private void Row_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains("modmgr.row.id")
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;

        // Project the cursor into the ScrollViewer's local coordinate space
        // and adjust the auto-scroll speed based on edge proximity.
        if (ModListScroll is null) return;
        EnsureAutoScrollTimer();
        var local = e.GetPosition(ModListScroll);
        UpdateAutoScrollFor(local, ModListScroll.Bounds.Height);
    }

    private void Row_Drop(object? sender, DragEventArgs e)
    {
        StopAutoScroll();
        if (DataContext is not MainViewModel vm) return;
        if (e.Data.Get("modmgr.row.id") is not string sourceId) return;

        // Find the ModRow whose Border the cursor is over.
        ModRow? target = null;
        if (e.Source is Visual hit)
        {
            for (var node = (Visual?)hit; node is not null; node = node.GetVisualParent())
            {
                if (node is Control ctrl && ctrl.DataContext is ModRow r) { target = r; break; }
            }
        }
        if (target is null || target.Id == sourceId) { e.Handled = true; return; }

        var targetIndex = vm.Mods.IndexOf(target);
        if (targetIndex < 0) { e.Handled = true; return; }

        vm.MoveRowToIndex(sourceId, targetIndex);
        e.Handled = true;
    }
}
