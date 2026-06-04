using Avalonia.Controls;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Icon nav rail down the left edge. Pure binding — every button
/// commands a <see cref="MainViewModel"/> page-switch or action.
/// </summary>
public partial class SideBar : UserControl
{
    public SideBar() => InitializeComponent();
}
