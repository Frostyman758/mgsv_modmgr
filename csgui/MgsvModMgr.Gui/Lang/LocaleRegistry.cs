using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MgsvModMgr.Gui.Lang;

/// <summary>
/// One discovered locale. <see cref="Code"/> is the bit between
/// <c>Strings.</c> and <c>.axaml</c> in the file name. <see cref="DisplayName"/>
/// is read from the <c>Str.Meta.LanguageName</c> key inside the file
/// (so it shows up in the dropdown in its own script).
/// <see cref="Dictionary"/> is the live <see cref="ResourceDictionary"/>
/// the runtime merges into <c>Application.Current.Resources</c>.
/// </summary>
public sealed record LocaleInfo(string Code, string DisplayName, ResourceDictionary Dictionary)
{
    /// <summary>So ComboBox renders the dictionary's own display name.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// Discovers every <c>Lang/Strings.*.axaml</c> in the app's assets at
/// startup and exposes a runtime <see cref="Apply"/> switch that swaps
/// the merged dictionary on <see cref="Application.Current"/>.
///
/// Adding a new language is just dropping
/// <c>Lang/Strings.&lt;code&gt;.axaml</c> next to the English one and
/// rebuilding — no code changes needed. Each file MUST define
/// <c>Str.Meta.LanguageName</c> (the dropdown label) and SHOULD cover
/// every <c>Str.*</c> key the English file defines (missing keys fall
/// back to the key itself, which surfaces gaps visibly in the UI).
/// </summary>
public static class LocaleRegistry
{
    private const string LangFolder = "avares://modmgr_gui/Lang/";

    private static List<LocaleInfo>? _all;

    /// <summary>Every locale discovered at startup, sorted by display name.</summary>
    public static IReadOnlyList<LocaleInfo> All => _all ??= Discover();

    /// <summary>The locale currently merged into Application.Resources.</summary>
    public static LocaleInfo? Current { get; private set; }

    /// <summary>
    /// Build-time discovered list of bundled locale codes. The csproj
    /// emits one <c>[assembly: AssemblyMetadata("BundledLocale", "Strings.&lt;code&gt;")]</c>
    /// attribute for every <c>Lang/Strings.*.axaml</c> file at compile
    /// time, so this enumeration is guaranteed to match exactly what
    /// was bundled — no probing, no exception storms, no code lists
    /// to maintain in sync. Adding a new locale = drop the .axaml
    /// file in Lang/ and rebuild; this list reflects it automatically.
    /// </summary>
    private const string FilenamePrefix = "Strings.";
    private static IEnumerable<string> BundledCodes()
    {
        var asm = typeof(LocaleRegistry).Assembly;
        foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key != "BundledLocale") continue;
            var v = attr.Value;
            if (string.IsNullOrEmpty(v)) continue;
            // Value is the raw %(Filename) — strip the "Strings." prefix
            // (the prefix-strip in MSBuild evaluates before item batching,
            // so it has to happen here instead).
            if (v.StartsWith(FilenamePrefix, StringComparison.Ordinal))
                v = v.Substring(FilenamePrefix.Length);
            if (v.Length > 0) yield return v;
        }
    }

    private static List<LocaleInfo> Discover()
    {
        var list = new List<LocaleInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in BundledCodes())
        {
            if (!seen.Add(code)) continue;
            var loaded = TryLoadByCode(code);
            if (loaded is not null) list.Add(loaded);
        }

        // Safety net: if the metadata-driven discovery turned up
        // nothing (a stripped or otherwise unusual build), force-try
        // English so the UI always has something to render.
        if (list.Count == 0)
        {
            var en = TryLoadByCode("en");
            if (en is not null) list.Add(en);
        }

        return list.OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static LocaleInfo? TryLoadByCode(string code)
    {
        // No AssetLoader.Exists() pre-check: it returns false for
        // compiled-XAML URIs even when AvaloniaXamlLoader.Load would
        // happily resolve them. Just attempt the load — most probes
        // miss (we walk a ~90-code ISO list against the ~2 locales
        // actually shipped) and each miss throws
        // Avalonia.Markup.Xaml.XamlLoadException ("No precompiled
        // XAML found for ..."). We swallow *every* exception here
        // rather than naming the type: a malformed bundled locale
        // file would throw a different XamlParseException / XamlIl
        // exception, and the right behaviour for "this locale file
        // is broken" is the same as "this locale isn't shipped" —
        // skip it, keep going, the dropdown still works minus that
        // one entry.
        var uri = new Uri(LangFolder + $"Strings.{code}.axaml");
        try
        {
            if (AvaloniaXamlLoader.Load(uri) is not ResourceDictionary dict) return null;
            var display = TryGetMetaName(dict) ?? code;
            return new LocaleInfo(code, display, dict);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pull the locale's display name out of the just-loaded dictionary.
    /// ResourceDictionary's API is a bit fragmented across Avalonia
    /// versions — we use the IDictionary indexer via ContainsKey, which
    /// is the stable surface across 11.x.
    /// </summary>
    private static string? TryGetMetaName(ResourceDictionary dict)
    {
        if (!dict.ContainsKey("Str.Meta.LanguageName")) return null;
        var v = dict["Str.Meta.LanguageName"];
        return v as string;
    }

    /// <summary>
    /// Look up by code. Returns English on miss; null only if even
    /// English failed to load (catastrophic — shouldn't happen).
    /// </summary>
    public static LocaleInfo? Find(string code)
        => All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
           ?? All.FirstOrDefault(l => l.Code == "en")
           ?? All.FirstOrDefault();

    /// <summary>
    /// Swap the merged language dictionary on <c>Application.Resources</c>.
    /// Existing <c>{DynamicResource Str.*}</c> bindings re-resolve
    /// automatically against the new dictionary — no XAML reload needed.
    /// </summary>
    public static void Apply(string code)
    {
        var app = Application.Current;
        if (app is null) return;
        var target = Find(code);
        if (target is null) return;

        // Strip any previously-merged language dict so keys don't shadow.
        // Identify by the meta-key — every locale file carries it, no
        // other dict does.
        var resources = app.Resources;
        var stale = resources.MergedDictionaries
            .OfType<ResourceDictionary>()
            .Where(d => d.ContainsKey("Str.Meta.LanguageName"))
            .ToList();
        foreach (var d in stale) resources.MergedDictionaries.Remove(d);

        resources.MergedDictionaries.Add(target.Dictionary);
        Current = target;
    }
}
