using System.Text.RegularExpressions;

namespace MgsvModMgr.Core;

/// <summary>
/// Cleans the BBCode + HTML soup the Nexus Mods description field
/// returns into something readable as plain text inside a TextBlock.
/// Avalonia TextBlock can render inline runs (bold/italic/link) if we
/// later want to upgrade — for now strip everything and just normalize
/// line breaks. Conservative on purpose: anything unrecognised is
/// stripped rather than reproduced, so we never accidentally render
/// unprocessed markup like the user just saw.
/// </summary>
public static class NexusDescription
{
    // BBCode tags written as [name] or [name=value] (close = [/name]).
    private static readonly Regex BBCodeTag = new(
        @"\[/?[a-zA-Z][a-zA-Z0-9]*(?:=[^\]]*)?\]",
        RegexOptions.Compiled);

    // <br>, <br/>, <br />, <BR/>, etc.
    private static readonly Regex HtmlBr = new(
        @"<\s*br\s*/?\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Any remaining HTML tag.
    private static readonly Regex HtmlTag = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    // 3+ newlines collapse to 2 (paragraph break).
    private static readonly Regex MultiNewline = new(
        @"\n{3,}",
        RegexOptions.Compiled);

    // 3+ spaces collapse to 1.
    private static readonly Regex MultiSpace = new(
        @"[ \t]{2,}",
        RegexOptions.Compiled);

    public static string CleanForDisplay(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw;

        // 1. <br /> ─→ newline before stripping the rest of HTML, so
        //    line breaks survive the tag-strip.
        s = HtmlBr.Replace(s, "\n");

        // 2. Strip all BBCode tags.
        s = BBCodeTag.Replace(s, "");

        // 3. Strip remaining HTML tags.
        s = HtmlTag.Replace(s, "");

        // 4. Decode the entities Nexus actually emits.
        s = s.Replace("&amp;",  "&")
             .Replace("&lt;",   "<")
             .Replace("&gt;",   ">")
             .Replace("&quot;", "\"")
             .Replace("&#39;",  "'")
             .Replace("&apos;", "'")
             .Replace("&nbsp;", " ");

        // 5. Normalize line endings + whitespace.
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = MultiNewline.Replace(s, "\n\n");
        s = MultiSpace.Replace(s, " ");

        return s.Trim();
    }
}
