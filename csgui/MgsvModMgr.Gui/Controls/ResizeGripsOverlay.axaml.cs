using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Manual edge-resize grips for platforms where the OS chrome was
/// stripped (Linux/macOS path in <see cref="MainWindow"/>). Each grip
/// border's <c>Tag</c> names the <see cref="WindowEdge"/> to resize
/// against; we delegate to <see cref="Window.BeginResizeDrag"/> which
/// routes to <c>_NET_WM_MOVERESIZE</c> on X11 and
/// <c>xdg_toplevel.resize</c> on Wayland — both supported by KWin,
/// Mutter, etc.
/// </summary>
public partial class ResizeGripsOverlay : UserControl
{
    public ResizeGripsOverlay() => InitializeComponent();

    private void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (this.GetVisualRoot() is not Window w) return;
        if (w.WindowState == WindowState.Maximized) return;
        if (sender is not Control c) return;
        if (c.Tag is not string s) return;
        if (!Enum.TryParse<WindowEdge>(s, ignoreCase: true, out var edge)) return;
        if (!e.GetCurrentPoint(w).Properties.IsLeftButtonPressed) return;
        w.BeginResizeDrag(edge, e);
    }
}
