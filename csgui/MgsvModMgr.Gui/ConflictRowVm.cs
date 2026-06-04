using System.Collections.Generic;
using System.Linq;
using MgsvModMgr.Core;

namespace MgsvModMgr.Gui;

/// <summary>
/// View-model for a single conflict row on the Log page's lower half.
/// Splits the raw <see cref="ConflictInfo"/> into the winning mod and
/// the overridden mods so the template can render them with different
/// affordances (winner chip vs strikethrough list).
/// </summary>
public sealed class ConflictRowVm
{
    public string       Path       { get; init; } = "";
    public string       Category   { get; init; } = "";
    public string       Winner     { get; init; } = "";
    public List<string> Overridden { get; init; } = new();

    public string OverriddenJoined => string.Join(", ", Overridden);
    public string CountSubtitle    => $"{Overridden.Count + 1} mods";

    public static ConflictRowVm From(ConflictInfo c) => new()
    {
        Path       = c.Path,
        Category   = c.Category,
        Winner     = c.Contributors.Count > 0 ? c.Contributors[0] : "",
        Overridden = c.Contributors.Skip(1).ToList(),
    };
}
