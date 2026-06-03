using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        finally { _dragSource = null; }
    }

    private void Row_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragArmed   = false;
        _dragSource  = null;
    }

    private void Row_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains("modmgr.row.id")
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Row_Drop(object? sender, DragEventArgs e)
    {
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
