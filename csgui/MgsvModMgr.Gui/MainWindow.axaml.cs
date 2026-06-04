using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MgsvModMgr.Gui;

// MainWindow code-behind — the composition root.
//
// The XAML side of MainWindow is now purely structural: it places the
// UserControls under Controls/ into a page-switching grid. The
// concerns that remain here are the ones that genuinely span multiple
// sections of the UI:
//   - DirtyBrush hue lerp (HookDirtyBrush) — drives every install-
//     state-sensitive control in the window
//   - window-wide drag-and-drop file accept (Window_DragOver/Drop)
//   - the search box's live "jump to row" handler, which needs both
//     the search TextBox (in ModsHeader) AND the DataGrid (in ModsList)
//   - the right-click column-visibility flyout on the mods header
//   - column-visibility persistence at startup
// Drag-reorder lives in MainWindow.DragReorder.cs and the custom
// chrome lives in MainWindow.ApplicationFrame.cs — both partials of
// this class.
public partial class MainWindow : Window
{
    // Endpoints for the live "dirty" colour ramp.
    //   mix = 1.0 → amber (full pending / fresh-dirty, no Apply running)
    //   mix = 0.0 → red   (clean, Apply finished)
    // During Apply, MainViewModel.DirtyMix glides 1 → 0 in lockstep with
    // ApplyProgress, so the brush — and every control bound through it —
    // shifts colour live as the install bar fills.
    private static readonly Color DirtyAmber = Color.FromRgb(0xB8, 0x90, 0x30);
    private static readonly Color DirtyRed   = Color.FromRgb(0xB6, 0x2A, 0x2A);

    private SolidColorBrush? _dirtyBrush;

    public MainWindow()
    {
        InitializeComponent();
        // Per-platform chrome: Windows keeps the XAML's ExtendClientArea
        // hint; Linux / macOS flip to SystemDecorations=None and reveal
        // the manual resize-grip overlay so KWin / Mutter don't double
        // up with their own title bar.
        ApplyPlatformChrome();
        // DevTools always on so the user can press F12 in the shipped
        // build, drill into any visual, and live-edit Margin / Padding /
        // brushes in the property grid.
        this.AttachDevTools();
        var vm = new MainViewModel(this);
        DataContext = vm;
        Opened += async (_, _) =>
        {
            await vm.OnOpenedAsync();
            // Apply persisted column-visibility prefs from state.txt
            // now that the DataGrid template is realised and the VM
            // has its ModManager loaded.
            ApplyHiddenColumnsFromState();
        };

        AttachGridHooks();
        HookDirtyBrush(vm);

        // Keep the maximise/restore button's glyph in sync with the
        // actual window state.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) RefreshMaxRestoreGlyph();
        };

        // Window-wide file drop: accept .mgsv files and any archive
        // SharpCompress can crack open.
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent,     Window_Drop);

        // Right-click anywhere on the mods page header (blank space
        // next to the title) → pops the column-visibility menu.
        // ModsHeader is in a UserControl now; we listen on its inner
        // Border (HeaderBorder), which the UserControl exposes.
        var hdr = ModsHeaderHost?.HeaderBorder;
        if (hdr is not null)
            hdr.AddHandler(
                ContextRequestedEvent,
                ModsHeader_ContextRequested,
                Avalonia.Interactivity.RoutingStrategies.Bubble);

        // The live "jump-to" search lives on the ModsHeader's TextBox.
        // The handler needs the DataGrid (in ModsList) to scroll, so
        // it stays on MainWindow — we just subscribe to the box.
        if (ModsHeaderHost?.SearchTextBox is { } sbox)
            sbox.TextChanged += SearchBox_TextChanged;
    }

    // Convenience accessors so the rest of this class (and the partial
    // siblings) can read the inner grids/canvases without going through
    // the UserControl property every time.
    private DataGrid? ModListGrid => ModsListHost?.GridControl;

    /// <summary>
    /// Wire MainViewModel.DirtyMix to the shared DirtyBrush resource. We
    /// mutate the same SolidColorBrush instance's Color property on every
    /// change rather than swapping the resource — Avalonia brushes notify
    /// their consumers when Color updates, so every control that bound
    /// {DynamicResource DirtyBrush} repaints in the same frame. This is
    /// what makes the install bar, toggle tracks, Add button and sidebar
    /// Apply pip all shift colour live as ApplyProgress moves.
    /// </summary>
    private void HookDirtyBrush(MainViewModel vm)
    {
        if (Application.Current?.Resources.TryGetValue("DirtyBrush", out var res) == true
            && res is SolidColorBrush brush)
        {
            _dirtyBrush = brush;
        }
        vm.PropertyChanged += OnVmPropertyChanged;
        UpdateDirtyBrush(vm.DirtyMix);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DirtyMix)
            && DataContext is MainViewModel vm)
            UpdateDirtyBrush(vm.DirtyMix);
    }

    private void UpdateDirtyBrush(double mix)
    {
        if (_dirtyBrush is null) return;
        mix = Math.Clamp(mix, 0.0, 1.0);
        // Linear RGB lerp between red (mix=0) and amber (mix=1).
        var r = (byte)Math.Round(DirtyRed.R + (DirtyAmber.R - DirtyRed.R) * mix);
        var g = (byte)Math.Round(DirtyRed.G + (DirtyAmber.G - DirtyRed.G) * mix);
        var b = (byte)Math.Round(DirtyRed.B + (DirtyAmber.B - DirtyRed.B) * mix);
        _dirtyBrush.Color = Color.FromRgb(r, g, b);
    }

    // ────────────────────────────────────────────────────────────────
    // File drag-drop onto the window: accept .mgsv + archives
    // ────────────────────────────────────────────────────────────────

    private static readonly string[] DroppableExtensions =
        { ".mgsv", ".zip", ".rar", ".7z", ".tar", ".gz" };

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        var ok = e.Data.Contains(DataFormats.Files) && AnyDroppedPathSupported(e);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainViewModel vm) return;
        var files = e.Data.GetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p) && IsSupportedExt(p!))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0) return;

        await vm.AddDroppedFilesAsync(paths);
    }

    private static bool AnyDroppedPathSupported(DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files is null) return false;
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(p) && IsSupportedExt(p!)) return true;
        }
        return false;
    }

    private static bool IsSupportedExt(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        foreach (var supported in DroppableExtensions)
            if (string.Equals(ext, supported, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ────────────────────────────────────────────────────────────────
    // Column visibility: right-click any column header for a toggle menu
    // ────────────────────────────────────────────────────────────────

    private static readonly string[] EssentialColumns = { "NAME", "ENABLED" };

    /// <summary>
    /// Walks each DataGrid column once at startup and flips IsVisible
    /// off for any column header listed in MainViewModel HiddenColumns
    /// (read from state.txt). Essentials are never touched.
    /// </summary>
    private void ApplyHiddenColumnsFromState()
    {
        if (ModListGrid is null) return;
        if (DataContext is not MainViewModel vm) return;
        foreach (var col in ModListGrid.Columns)
        {
            var header = col.Header?.ToString() ?? "";
            if (Array.IndexOf(EssentialColumns, header) >= 0) continue;
            col.IsVisible = !vm.IsColumnHidden(header);
        }
    }

    private void ModsHeader_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        try
        {
            var hdr = ModsHeaderHost?.HeaderBorder;
            if (ModListGrid is null || hdr is null) return;

            // Ignore right-clicks that landed on the toolbar buttons
            // (Up/Down/Search/Trash/Add).
            if (e.Source is Visual src)
            {
                for (var n = src; n is not null && !ReferenceEquals(n, hdr); n = n.GetVisualParent())
                {
                    if (n is Button) return;
                }
            }

            var flyout = new MenuFlyout
            {
                Placement = PlacementMode.Pointer,
            };
            foreach (var col in ModListGrid.Columns)
            {
                var header = col.Header?.ToString() ?? "";
                if (Array.IndexOf(EssentialColumns, header) >= 0) continue;

                var item = new MenuItem
                {
                    Header     = header,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked  = col.IsVisible,
                };
                var capturedCol  = col;
                var capturedItem = item;
                var capturedName = header;
                item.Click += (_, _) =>
                {
                    capturedCol.IsVisible  = !capturedCol.IsVisible;
                    capturedItem.IsChecked = capturedCol.IsVisible;
                    if (DataContext is MainViewModel mvm)
                        mvm.SetColumnHidden(capturedName, !capturedCol.IsVisible);
                };
                flyout.Items.Add(item);
            }

            flyout.ShowAt(hdr);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            // Never let a right-click bring the app down.
            System.Diagnostics.Debug.WriteLine($"[header menu] {ex}");
        }
    }

    /// <summary>
    /// Live "jump-to" search: on every keystroke, find the first mod
    /// whose Name or Id contains the query (case-insensitive), select
    /// it, and scroll the DataGrid until it's visible. No filtering —
    /// the list stays intact, the user just gets fast navigation.
    /// </summary>
    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (ModListGrid is null) return;
        var q = ModsHeaderHost?.SearchTextBox?.Text;
        if (string.IsNullOrWhiteSpace(q)) return;

        var match = vm.Mods.FirstOrDefault(m =>
            (m.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (m.Id?.Contains(q, StringComparison.OrdinalIgnoreCase)   ?? false));
        if (match is null) return;

        vm.SelectedMod = match;
        try { ModListGrid.ScrollIntoView(match, null); } catch { }
    }
}
