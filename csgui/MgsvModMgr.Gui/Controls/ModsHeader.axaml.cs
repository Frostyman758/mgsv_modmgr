using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Header strip for the Installed Mods page: title + subtitle on the
/// left, move-up / move-down / search pill / remove / add toolbar on
/// the right.
///
/// The search pill collapse/expand state machine lives entirely in
/// this control. The text-changed signal that drives the live jump-
/// to-row search is observed by <see cref="MainWindow"/> because that
/// needs a reference to the DataGrid — this control exposes
/// <see cref="SearchTextBox"/> and <see cref="HeaderBorder"/> so the
/// parent can attach to the right elements.
/// </summary>
public partial class ModsHeader : UserControl
{
    public ModsHeader() => InitializeComponent();

    /// <summary>The outer Border the parent uses as the ContextRequested target.</summary>
    public Border HeaderBorder    => HeaderRoot;
    /// <summary>The search TextBox. Parent subscribes to TextChanged.</summary>
    public TextBox SearchTextBox  => SearchBox;

    private const double SearchExpandedWidth = 280;

    private void SearchPill_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only respond when collapsed — once expanded, clicks inside go
        // to the TextBox and should not re-trigger expansion logic.
        if (SearchBox.IsVisible) return;
        ExpandSearch();
        e.Handled = true;
    }

    private void ExpandSearch()
    {
        SearchBox.IsVisible = true;
        SearchPill.Width    = SearchExpandedWidth;
        if (!SearchPill.Classes.Contains("expanded"))
            SearchPill.Classes.Add("expanded");
        // Focus on the next dispatcher tick so the visibility flip has
        // propagated through layout before the focus call lands.
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
    }

    private void CollapseSearch()
    {
        SearchBox.IsVisible = false;
        SearchPill.Width    = 40;
        SearchPill.Classes.Remove("expanded");
    }

    private void SearchBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        // Auto-collapse only if the user didn't actually search anything.
        if (string.IsNullOrEmpty(SearchBox.Text)) CollapseSearch();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            CollapseSearch();
            e.Handled = true;
        }
    }
}
