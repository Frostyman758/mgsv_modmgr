using Avalonia.Controls;
using Avalonia.Input;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Bottom status strip: shows the resolved game root + datfpk paths
/// and the live Apply progress bar. A click anywhere on the strip
/// hides it (it auto-reappears when the next Apply starts).
/// </summary>
public partial class Footer : UserControl
{
    public Footer() => InitializeComponent();

    private void Footer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.HideFooter();
    }
}
