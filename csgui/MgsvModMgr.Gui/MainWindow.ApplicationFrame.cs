using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MgsvModMgr.Gui;

// MainWindow — custom Application Frame.
//
// Two-mode chrome:
// - WINDOWS: ExtendClientAreaToDecorationsHint=true + ChromeHints=NoChrome.
//   We paint over the entire client area while the OS keeps providing
//   edge resize, snap-to-edge, and Win-arrow gestures. (XAML defaults.)
// - LINUX/MACOS: many WMs (notably KWin) ignore the ExtendClientArea
//   hint and draw their own title bar regardless, producing a
//   double-title-bar effect. The constructor flips to
//   SystemDecorations=None on those platforms and reveals the manual
//   resize-grip overlay (defined in XAML, hidden by default) so we
//   carry our own edge-drag affordance.
public partial class MainWindow
{
    /// <summary>
    /// Apply platform-specific window chrome at construction time.
    /// XAML attributes assume the Windows case; this overrides them
    /// when we detect a non-Windows OS.
    /// </summary>
    private void ApplyPlatformChrome()
    {
        if (OperatingSystem.IsWindows()) return;

        // Linux + macOS: strip native chrome entirely and rely on our
        // overlay grips for edge-resize. KWin/Mutter/etc. honour
        // SystemDecorations=None cleanly and don't draw a fallback
        // title bar over us. BeginResizeDrag works fine on these
        // platforms via _NET_WM_MOVERESIZE (X11) / xdg_toplevel.resize
        // (Wayland) / NSWindow drag (macOS).
        SystemDecorations                  = Avalonia.Controls.SystemDecorations.None;
        ExtendClientAreaToDecorationsHint  = false;
        ExtendClientAreaChromeHints        = Avalonia.Platform.ExtendClientAreaChromeHints.Default;
        ExtendClientAreaTitleBarHeightHint = 0;
        if (ResizeGripsOverlay is not null) ResizeGripsOverlay.IsVisible = true;
    }

    /// <summary>
    /// Single shared resize-grip handler. Each grip Border carries
    /// its <see cref="WindowEdge"/> name in <c>Tag</c>, parsed here
    /// and fed to <see cref="Window.BeginResizeDrag"/>. No-op while
    /// the window is maximised.
    /// </summary>
    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) return;
        if (sender is not Control c) return;
        if (c.Tag is not string s) return;
        if (!Enum.TryParse<WindowEdge>(s, ignoreCase: true, out var edge)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginResizeDrag(edge, e);
    }

    // ────────────────────────────────────────────────────────────────
    // Custom title bar handlers — move / minimise / maximise / close.
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Single-click drag on the title bar moves the window. The
    /// double-tap event handler below catches the second click, so
    /// a single press always triggers BeginMoveDrag without fighting
    /// the maximise-toggle gesture.
    /// </summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>Double-tap the title bar → toggle maximise.</summary>
    private void TitleBar_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        => ToggleMaximize();

    private void WindowBtn_Minimize(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void WindowBtn_MaximizeRestore(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleMaximize();

    private void WindowBtn_Close(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>
    /// Show the "restore" glyph when the window is currently maximised,
    /// otherwise show the "maximise" glyph. Fluent UI System Icons:
    /// F0F1 = square (maximise), EBF6 = maximize-restore (restore).
    /// </summary>
    private void RefreshMaxRestoreGlyph()
    {
        if (MaxRestoreButton is null) return;
        MaxRestoreButton.Content = WindowState == WindowState.Maximized
            ? ""  // restore
            : ""; // maximise (square)
    }

}