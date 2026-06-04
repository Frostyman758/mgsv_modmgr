using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Nexus browser page body — empty state, loading state, error card,
/// card grid (with infinite scroll), and the per-mod detail view. All
/// data and visibility are driven by <see cref="MainViewModel"/>; this
/// control just hosts the three click handlers (infinite-scroll
/// trigger, card-press → detail nav, tag-chip → tag-filter).
/// </summary>
public partial class NexusBrowser : UserControl
{
    public NexusBrowser() => InitializeComponent();

    /// <summary>
    /// Infinite scroll: when the cursor is within ~600 px of the bottom
    /// of the card grid, ask the VM for the next page. The VM gates the
    /// call against CanLoadMoreNexus, so repeated fires during scroll
    /// inertia are harmless.
    /// </summary>
    private void NexusGridScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not MainViewModel vm) return;
        var bottomGap = sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height);
        if (bottomGap < 600 && vm.CanLoadMoreNexus)
            _ = vm.LoadMoreNexusAsync();
    }

    private void NexusCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Click anywhere on the card → drill into the detail view for
        // that mod. The card's DataContext is the NexusModCard binding
        // target; the page-level VM owns the selection state.
        if (sender is Border b
            && b.DataContext is NexusModCard card
            && DataContext is MainViewModel vm)
        {
            vm.NexusSelectedMod = card;
            e.Handled = true;
        }
    }

    private void NexusTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag && DataContext is MainViewModel vm)
            vm.FilterByTag(tag);
    }
}
