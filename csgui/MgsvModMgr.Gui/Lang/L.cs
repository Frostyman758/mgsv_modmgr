using System.Globalization;
using Avalonia;

namespace MgsvModMgr.Gui.Lang;

/// <summary>
/// Code-behind accessor for the localised strings stored as application
/// resources in <c>Lang/Strings.en.axaml</c> (and future siblings).
///
/// XAML reads the dictionary directly via
/// <c>{DynamicResource Str.Whatever}</c>. C# call sites use <see cref="S"/>
/// (lookup) or <see cref="F"/> (lookup + <see cref="string.Format(string, object?[])"/>).
///
/// Returns the key itself on miss so a typo surfaces in the UI instead
/// of crashing — easier to spot during translation work than an
/// exception in a deep call stack.
/// </summary>
public static class L
{
    /// <summary>Look up a string by resource key.</summary>
    public static string S(string key)
    {
        var app = Application.Current;
        if (app is not null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v)
            && v is string s)
        {
            return s;
        }
        return key;
    }

    /// <summary>Look up a string and format it with the given args.</summary>
    public static string F(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, S(key), args);
}
