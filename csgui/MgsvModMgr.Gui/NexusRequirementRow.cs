namespace MgsvModMgr.Gui;

/// <summary>
/// Row VM for a single requirement on the Nexus mod detail page.
/// Maps the v2 GraphQL <c>ModRequirement</c> shape onto something
/// XAML-bindable. The mod name and notes display inline; <see cref="Url"/>
/// drives the "Open" button so the user can jump to that requirement
/// on nexusmods.com in the system browser.
/// </summary>
public sealed class NexusRequirementRow
{
    public string ModName              { get; init; } = "";
    public string Notes                { get; init; } = "";
    public string Url                  { get; init; } = "";
    /// <summary>If true, the requirement isn't a Nexus mod — points outside the site.</summary>
    public bool   ExternalRequirement  { get; init; }

    public bool HasNotes               => !string.IsNullOrWhiteSpace(Notes);
    public string Label                => ExternalRequirement ? "External" : "Nexus mod";
}
