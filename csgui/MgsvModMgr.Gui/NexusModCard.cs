using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace MgsvModMgr.Gui;

/// <summary>
/// View-model for a single mod tile rendered on the Nexus Mods browser
/// page. Plain init-only metadata + a mutable <see cref="Thumbnail"/>
/// that the VM swaps in once the picture_url has been fetched off the
/// CDN. INotifyPropertyChanged so the Image / placeholder swap is live.
/// </summary>
public sealed class NexusModCard : INotifyPropertyChanged
{
    public int     ModId        { get; init; }
    public string  Name         { get; init; } = "";
    public string  Author       { get; init; } = "";
    public string  Category     { get; init; } = "";
    public string  Summary      { get; init; } = "";
    public string? PictureUrl   { get; init; }
    public int     Endorsements { get; init; }
    public int     Downloads    { get; init; }
    public string  Version      { get; init; } = "";
    /// <summary>Direct nexusmods.com URL for the "Open in browser" affordance.</summary>
    public string  WebUrl       { get; init; } = "";

    private Bitmap? _thumbnail;
    /// <summary>
    /// Loaded asynchronously off PictureUrl. Null while in-flight or
    /// on fetch failure — the card template shows a placeholder glyph
    /// in either case so we never render an empty grey box.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }
    public bool HasThumbnail => _thumbnail is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
