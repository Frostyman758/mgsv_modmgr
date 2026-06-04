using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MgsvModMgr.Gui;

// MainWindow — custom Application Frame (title bar + min/max/close
// buttons + drag/double-tap behaviour + maximise-glyph swap).
// SystemDecorations are extended via ExtendClientAreaToDecorationsHint
// so the OS still handles edge resize, snap-to-edge, and Win-arrow
// gestures; we just draw over the whole client area.
public partial class MainWindow
{
    // ────────────────────────────────────────────────────────────────
    // Custom title bar handlers. ExtendClientAreaToDecorationsHint
    // hands us the full client area while the OS keeps providing
    // edge resize + snap-to-edge + Win-arrow gestures, so we don't
    // need our own resize-grip handlers any more — only the move /
    // minimise / maximise-restore / close behaviour.
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