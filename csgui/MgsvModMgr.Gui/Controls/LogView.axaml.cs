using Avalonia.Controls;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Activity-log + file-conflict viewer. Pure binding — the log text
/// and the conflict list are both pushed by the view-model.
/// </summary>
public partial class LogView : UserControl
{
    public LogView() => InitializeComponent();
}
