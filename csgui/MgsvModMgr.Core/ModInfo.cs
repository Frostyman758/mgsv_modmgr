namespace MgsvModMgr.Core;

public sealed class ModInfo
{
    public string Id      { get; set; } = "";
    public string Name    { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author  { get; set; } = "";
    public bool   Enabled { get; set; } = true;
    public string Source  { get; set; } = "";

    /// <summary>
    /// True when this mod's current Enabled+content state is reflected in
    /// the live game install. Cleared by Add / toggle / Move / Revert.
    /// Set by Apply for every mod that was enabled at the time the apply ran.
    /// </summary>
    public bool   Applied { get; set; } = false;

    public List<string>                       QarPaths       { get; } = new();
    public Dictionary<string, string>         QarHashes      { get; } = new();   // vpath -> decimal uint64
    public Dictionary<string, List<string>>   FpkEntries     { get; } = new();   // host -> inner vpaths
    public List<string>                       GameDirEntries { get; } = new();   // rel paths under GameDir/
}
