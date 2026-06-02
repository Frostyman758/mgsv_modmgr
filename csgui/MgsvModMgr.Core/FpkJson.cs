using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MgsvModMgr.Core;

/// <summary>
/// Parser/writer for the JSON manifest <c>datfpk.exe</c> emits beside every
/// unpacked .fpk(d). The schema, distilled from datfpk's source:
/// <code>
/// {
///   "type":       "fpk" | "fpkd",
///   "entries":    [ { "filePath": "..." }, ... ],
///   "references": [ { "filePath": "..." }, ... ]   // optional
/// }
/// </code>
/// </summary>
public static class FpkJson
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
    };

    /// <summary>Read the <c>type</c> field, defaulting to <c>"fpkd"</c>.</summary>
    public static string ReadType(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        return (string?)root?["type"] ?? "fpkd";
    }

    /// <summary>Read the <c>references[*].filePath</c> values in declaration order.</summary>
    public static IReadOnlyList<string> ReadReferences(string json)
    {
        var refs = new List<string>();
        if (JsonNode.Parse(json) is not JsonObject root) return refs;
        if (root["references"] is not JsonArray arr)    return refs;

        foreach (var node in arr)
        {
            var fp = (string?)node?["filePath"];
            if (!string.IsNullOrEmpty(fp)) refs.Add(fp);
        }
        return refs;
    }

    /// <summary>
    /// Serialise a manifest covering the given entries and (optional) references.
    /// Both lists are written in the order supplied.
    /// </summary>
    public static string Write(string type, IEnumerable<string> entries, IEnumerable<string> references)
    {
        var entriesArr = new JsonArray();
        foreach (var e in entries)    entriesArr.Add(new JsonObject { ["filePath"] = e });

        var referencesArr = new JsonArray();
        foreach (var r in references) referencesArr.Add(new JsonObject { ["filePath"] = r });

        var doc = new JsonObject
        {
            ["type"]    = type,
            ["entries"] = entriesArr,
        };
        if (referencesArr.Count > 0) doc["references"] = referencesArr;

        return doc.ToJsonString(Indented) + "\n";
    }

    /// <summary>Read a manifest from disk.</summary>
    public static string Read(string path) => File.ReadAllText(path);
}
