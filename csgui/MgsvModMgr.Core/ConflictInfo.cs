namespace MgsvModMgr.Core;

/// <summary>
/// A single file path that two or more enabled mods both want to provide.
/// The first entry in <see cref="Contributors"/> is the load-order winner
/// (top of the list = highest priority); everything after is overridden.
/// </summary>
public sealed class ConflictInfo
{
    /// <summary>Game-relative path the mods are competing for.</summary>
    public string Path { get; init; } = "";
    /// <summary>QAR / GAMEDIR / FPK — which layer of the install the conflict lives in.</summary>
    public string Category { get; init; } = "";
    /// <summary>
    /// Mod ids in load-order priority. <c>Contributors[0]</c> is the
    /// winning mod; later entries are the ones whose version of this
    /// file gets overwritten by Apply.
    /// </summary>
    public List<string> Contributors { get; init; } = new();
}
