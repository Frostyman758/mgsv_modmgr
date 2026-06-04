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
        // DevTools always on so the user can press F12 in the shipped
        // build, drill into any visual, and live-edit Margin / Padding /
        // brushes in the property grid. Avalonia.Diagnostics is already
        // referenced; this just wires up the F12 binding.
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

        // Window-wide file drop: accept .mgsv files and any archive
        // SharpCompress can crack open. Hand the paths to the VM's
        // add-mod pipeline which handles extraction + registration.
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent,     Window_Drop);

        // Right-click anywhere on the page header (blank area next to
        // "Installed mods" / between the title and the toolbar) → pops
        // the column-visibility menu. NAME and ENABLED are filtered
        // out as essentials.
        if (ModsHeader is not null)
            ModsHeader.AddHandler(
                ContextRequestedEvent,
                ModsHeader_ContextRequested,
                Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

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

    private void Footer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.HideFooter();
    }

    // ────────────────────────────────────────────────────────────────
    // File drag-drop onto the window: accept .mgsv + archives
    // ────────────────────────────────────────────────────────────────

    private static readonly string[] DroppableExtensions =
        { ".mgsv", ".zip", ".rar", ".7z", ".tar", ".gz" };

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        // Accept files only — refuse anything else (text, links, etc).
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

    /// <summary>
    /// Infinite scroll: when the user is within ~600 px of the bottom
    /// of the Nexus card grid, request the next page from the VM. The
    /// VM gates the call against CanLoadMoreNexus, so repeated fires
    /// during the scroll animation are harmless.
    /// </summary>
    private void NexusTag_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag && DataContext is MainViewModel vm)
            vm.FilterByTag(tag);
    }

    private void NexusGridScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not MainViewModel vm) return;
        var bottomGap = sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height);
        if (bottomGap < 600 && vm.CanLoadMoreNexus)
            _ = vm.LoadMoreNexusAsync();
    }

    private void NexusCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Click anywhere on the card → drill into the detail view for
        // that mod. The card's DataContext is the NexusModCard binding
        // target; the page-level VM owns the selection state.
        if (sender is Border b
            && b.DataContext is NexusModCard card
            && DataContext is MainViewModel vm)
        {
            vm.NexusSelectedMod = card;
            e.Handled = true;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Search pill: collapses ↔ expands via the Border's Width transition.
    // ────────────────────────────────────────────────────────────────

    private const double SearchExpandedWidth = 280;

    private void SearchPill_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only respond when collapsed — once expanded, clicks inside go
        // to the TextBox and should not re-trigger expansion logic.
        if (SearchBox is null || SearchBox.IsVisible) return;
        ExpandSearch();
        e.Handled = true;
    }

    private void ExpandSearch()
    {
        if (SearchPill is null || SearchBox is null) return;
        SearchBox.IsVisible = true;
        SearchPill.Width    = SearchExpandedWidth;
        // Toggle the .expanded pseudo-class on the outer pill; the
        // Theme.axaml styles take it from there (red backplate + white
        // glyph) — keeps brush ownership inside XAML where DirtyBrush
        // live-shifts cleanly without code-behind interpolation.
        if (!SearchPill.Classes.Contains("expanded"))
            SearchPill.Classes.Add("expanded");
        // Focus on the next dispatcher tick so the visibility flip has
        // propagated through layout before the focus call lands.
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
    }

    private void CollapseSearch()
    {
        if (SearchPill is null || SearchBox is null) return;
        SearchBox.IsVisible = false;
        SearchPill.Width    = 40;
        SearchPill.Classes.Remove("expanded");
    }

    private void SearchBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Auto-collapse only if the user didn't actually search anything.
        // If they typed a query, keep the bar open so the filter stays
        // visible/visible-feedback even when focus moves elsewhere.
        if (string.IsNullOrEmpty(SearchBox?.Text)) CollapseSearch();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox is not null) SearchBox.Text = "";
            CollapseSearch();
            e.Handled = true;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Column visibility: right-click any column header for a toggle menu
    // ────────────────────────────────────────────────────────────────

    private static readonly string[] EssentialColumns = { "NAME", "ENABLED" };

    /// <summary>
    /// Walks each DataGrid column once at startup and flips IsVisible
    /// off for any column header that's listed in MainViewModel
    /// HiddenColumns (read from state.txt). Essentials are never
    /// touched even if a stale state file lists them.
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
            if (ModListGrid is null || ModsHeader is null) return;

            // Ignore right-clicks that landed on the toolbar buttons
            // (Up/Down/Search/Trash/Add) — those should keep their own
            // default behaviour. Anything else inside the header strip
            // (title text, blank space) pops the column menu.
            if (e.Source is Visual src)
            {
                for (var n = src; n is not null && !ReferenceEquals(n, ModsHeader); n = n.GetVisualParent())
                {
                    if (n is Button) return;
                }
            }

            var flyout = new MenuFlyout
            {
                // Pops up exactly where the user right-clicked, not
                // pinned to the header strip's bottom edge.
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
                    // Persist: hidden=true → write hidecol=NAME to
                    // state.txt; visible=true → remove that line.
                    if (DataContext is MainViewModel mvm)
                        mvm.SetColumnHidden(capturedName, !capturedCol.IsVisible);
                };
                flyout.Items.Add(item);
            }

            flyout.ShowAt(ModsHeader);
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
        var q = SearchBox?.Text;
        if (string.IsNullOrWhiteSpace(q)) return;

        var match = vm.Mods.FirstOrDefault(m =>
            (m.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (m.Id?.Contains(q, StringComparison.OrdinalIgnoreCase)   ?? false));
        if (match is null) return;

        vm.SelectedMod = match;
        try { ModListGrid.ScrollIntoView(match, null); } catch { }
    }
}