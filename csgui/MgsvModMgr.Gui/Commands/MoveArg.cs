using Avalonia;

namespace MgsvModMgr.Gui.Commands;

/// <summary>
/// Carrier object bound into the row-context-menu "Move up" / "Move down"
/// items so a single command can apply to either direction depending on
/// which menu item fired it.
/// </summary>
public sealed class MoveArg : AvaloniaObject
{
    public static readonly StyledProperty<int>     DeltaProperty =
        AvaloniaProperty.Register<MoveArg, int>(nameof(Delta));

    public static readonly StyledProperty<ModRow?> RowProperty =
        AvaloniaProperty.Register<MoveArg, ModRow?>(nameof(Row));

    public int     Delta { get => GetValue(DeltaProperty); set => SetValue(DeltaProperty, value); }
    public ModRow? Row   { get => GetValue(RowProperty);   set => SetValue(RowProperty, value); }
}
