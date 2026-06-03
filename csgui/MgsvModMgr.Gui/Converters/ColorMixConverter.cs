using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MgsvModMgr.Gui.Converters;

/// <summary>
/// Linearly interpolates between two hex colours based on a 0..1 ratio.
/// Used to keep the yellow→red transition synced with apply progress
/// across multiple unrelated controls (progress bar fill, primary
/// button background, sidebar apply icon, etc.).
///
/// Binding usage:
///   {Binding DirtyMix,
///            Converter={x:Static converters:ColorMixConverter.Instance},
///            ConverterParameter='#B62A2A|#B89030'}
///
/// Parameter format: two #RRGGBB colours separated by '|'. Ratio 0 yields
/// the first colour (the "clean / applied" state), ratio 1 yields the
/// second (the "dirty / pending" state). Anything in between is the
/// per-channel linear blend.
/// </summary>
public sealed class ColorMixConverter : IValueConverter
{
    public static readonly ColorMixConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double mix) return null;
        if (parameter is not string spec) return null;
        var parts = spec.Split('|');
        if (parts.Length != 2) return null;
        if (!Color.TryParse(parts[0], out var clean)) return null;
        if (!Color.TryParse(parts[1], out var dirty)) return null;

        mix = Math.Clamp(mix, 0.0, 1.0);
        byte a = (byte)(clean.A + (dirty.A - clean.A) * mix);
        byte r = (byte)(clean.R + (dirty.R - clean.R) * mix);
        byte g = (byte)(clean.G + (dirty.G - clean.G) * mix);
        byte b = (byte)(clean.B + (dirty.B - clean.B) * mix);
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
