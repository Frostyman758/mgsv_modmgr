using System;
using Avalonia.Controls;

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
//   resize-grip overlay (a ResizeGripsOverlay UserControl, hidden by
//   default) so we carry our own edge-drag affordance.
//
// The title bar handlers (drag-move, double-tap maximise, min/max/close
// buttons) and the resize-grip pointer handlers now live inside their
// respective UserControls (Controls/TitleBar, Controls/ResizeGripsOverlay)
// and find their hosting Window via GetVisualRoot at click time.
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
        // title bar over us.
        SystemDecorations                  = Avalonia.Controls.SystemDecorations.None;
        ExtendClientAreaToDecorationsHint  = false;
        ExtendClientAreaChromeHints        = Avalonia.Platform.ExtendClientAreaChromeHints.Default;
        ExtendClientAreaTitleBarHeightHint = 0;
        if (ResizeGripsHost is not null) ResizeGripsHost.IsVisible = true;
    }

    /// <summary>
    /// Show the "restore" glyph when the window is currently maximised,
    /// otherwise show the "maximise" glyph. Fluent UI System Icons:
    /// F0F1 = square (maximise), EBF6 = maximize-restore (restore).
    /// </summary>
    private void RefreshMaxRestoreGlyph()
    {
        var btn = TitleBarHost?.MaxRestore;
        if (btn is null) return;
        btn.Content = WindowState == WindowState.Maximized
            ? ""  // restore (maximize-restore)
            : ""; // maximise (square)
    }
}
