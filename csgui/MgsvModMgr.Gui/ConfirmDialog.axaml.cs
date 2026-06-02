using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MgsvModMgr.Gui;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() { InitializeComponent(); }

    public ConfirmDialog(string body, string title = "Confirm", bool errorMode = false) : this()
    {
        Title          = title;
        TitleText.Text = title;
        BodyText.Text  = body;
        if (errorMode)
        {
            OkBtn.IsVisible   = false;
            CancelBtn.Content = "Close";
            IconText.Text     = "";  // Fluent ErrorBadge
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)      => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e)  => Close(false);
}
