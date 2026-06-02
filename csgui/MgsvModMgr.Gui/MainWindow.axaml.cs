using Avalonia.Controls;

namespace MgsvModMgr.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        var vm = new MainViewModel(this);
        DataContext = vm;
        Opened += async (_, _) => await vm.OnOpenedAsync();
    }
}
