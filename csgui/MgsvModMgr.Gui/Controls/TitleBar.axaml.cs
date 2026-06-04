using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Custom window chrome title bar. Lives inside <see cref="MainWindow"/>
/// at the very top; on Linux/macOS where the OS decorations are stripped
/// this is the user's only handle for moving / maximising / closing the
/// window. On Windows it sits over the OS title bar via the
/// ExtendClientArea hint, providing identical visual chrome cross-platform.
///
/// Handlers reach up to the hosting <see cref="Window"/> via
/// <see cref="Visual.GetVisualRoot"/> rather than holding a reference,
/// so the control is reusable in any window context.
/// </summary>
public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The maximise/restore button. Exposed so
    /// <c>MainWindow.RefreshMaxRestoreGlyph</c> can swap its Content
    /// between the maximise and restore glyphs when WindowState flips.
    /// </summary>
    public Button MaxRestore => MaxRestoreButton;

    private Window? Host => this.GetVisualRoot() as Window;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Host?.BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
        => ToggleMaximize();

    private void WindowBtn_Minimize(object? sender, RoutedEventArgs e)
    {
        if (Host is { } w) w.WindowState = WindowState.Minimized;
    }

    private void WindowBtn_MaximizeRestore(object? sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void WindowBtn_Close(object? sender, RoutedEventArgs e)
        => Host?.Close();

    private void ToggleMaximize()
    {
        if (Host is not { } w) return;
        w.WindowState = w.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
