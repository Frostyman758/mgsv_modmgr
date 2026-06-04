using Avalonia.Controls;

namespace MgsvModMgr.Gui.Controls;

/// <summary>
/// Drag-reorder visual overlay: a non-hit-testable canvas hosting the
/// row ghost and insertion-indicator line. The drag mechanics live in
/// <c>MainWindow.DragReorder.cs</c>; this control just exposes the
/// inner elements via public properties so the parent can drive them.
/// </summary>
public partial class DragOverlay : UserControl
{
    public DragOverlay() => InitializeComponent();

    /// <summary>The Canvas the parent writes ghost / insert-line
    /// positions into. Exposed so drag-reorder can pass it to
    /// <see cref="Avalonia.Input.PointerEventArgs.GetPosition"/> and
    /// <see cref="Visual.TransformToVisual"/> — that way the coords
    /// it reads back already live in the Canvas's local space, so
    /// writing them straight into Canvas.SetLeft/Top "just works"
    /// regardless of where the overlay is mounted in the parent grid.</summary>
    public Canvas Surface          => OverlayRoot;
    /// <summary>The ghost row that follows the cursor while dragging.</summary>
    public Border GhostBorder      => Ghost;
    /// <summary>The text inside the ghost (the dragged mod's name).</summary>
    public TextBlock GhostTextBlock => GhostText;
    /// <summary>The thin insertion-indicator line.</summary>
    public Border InsertIndicator  => InsertLine;
}
