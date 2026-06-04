using Avalonia.Controls;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// The Installed Mods DataGrid + row context menu. The drag-reorder
/// machinery and column-visibility persistence live on
/// <see cref="MainWindow"/> — this control just exposes
/// <see cref="GridControl"/> so they can reach the grid.
/// </summary>
public partial class ModsList : UserControl
{
    public ModsList() => InitializeComponent();

    /// <summary>The underlying DataGrid (drag-reorder, ScrollIntoView).</summary>
    public DataGrid GridControl => ModListGrid;
}
