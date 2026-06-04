using Avalonia.Controls;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Settings page body — game root / datfpk / Nexus API key fields,
/// theme toggle, save/cancel, and the Maintenance reset action. Pure
/// binding — no event handlers.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
