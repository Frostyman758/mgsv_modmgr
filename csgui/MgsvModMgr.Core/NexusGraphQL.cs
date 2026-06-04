using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MgsvModMgr.Core;

/// <summary>
/// Thin POST helper around the Nexus Mods GraphQL v2 endpoint. Auth is
/// the same <c>apikey</c> header v1 REST uses, sent on every call —
/// many v2 fields are public-readable, but our caller doesn't need to
/// branch on that, so we always send the header when we have one.
/// </summary>
public sealed class NexusGraphQL
{
    public const string Endpoint = "https://api.nexusmods.com/v2/graphql";

    private static readonly HttpClient _http = MakeHttpClient();
    private static HttpClient MakeHttpClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("mgsv_modmgr/1.0 (+https://github.com)");
        return h;
    }

    private readonly string _apiKey;
    public NexusGraphQL(string apiKey) { _apiKey = apiKey; }

    /// <summary>
    /// Send a raw GraphQL query and return the response body as a
    /// JSON string. Caller is responsible for parsing — useful during
    /// schema discovery where we don't know the response shape yet.
    /// </summary>
    public async Task<string> ExecuteRawAsync(string query, object? variables = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query,
            variables = variables ?? new { },
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        req.Headers.Add("apikey", _apiKey);
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GraphQL HTTP {(int)resp.StatusCode}: {body}");
        return body;
    }

    /// <summary>
    /// The four sort orders the browser page surfaces; mapped to
    /// concrete <c>ModsSort</c> partials at request time.
    /// </summary>
    public enum Sort { Trending, LatestAdded, LatestUpdated, MostDownloaded }

    /// <summary>
    /// Paginated mod-listing query. Backs every browse + search call:
    /// trending sorts by endorsements DESC, latest-added by createdAt
    /// DESC, latest-updated by updatedAt DESC. <paramref name="search"/>
    /// drives a wildcard match against the mod name; passing null /
    /// empty disables the name filter.
    /// </summary>
    public async Task<NexusModPage> ListModsAsync(
        string gameDomainName,
        Sort sort = Sort.Trending,
        int offset = 0,
        int count = 24,
        string? search = null,
        string? author = null,
        string? category = null,
        string? tag = null)
    {
        // Build the ModsSort partial inline-as-JSON so each sort enum
        // maps to a one-field input object that v2 expects.
        object sortValue = sort switch
        {
            Sort.LatestAdded    => new { createdAt    = new { direction = "DESC" } },
            Sort.LatestUpdated  => new { updatedAt    = new { direction = "DESC" } },
            Sort.MostDownloaded => new { downloads    = new { direction = "DESC" } },
            _                   => new { endorsements = new { direction = "DESC" } },
        };

        // ModsFilter: game first (always required), name as an
        // optional WILDCARD if the caller passed a search string.
        var filter = new Dictionary<string, object>
        {
            ["op"]             = "AND",
            ["gameDomainName"] = new[] { new { value = gameDomainName, op = "EQUALS" } },
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            filter["name"] = new[] { new { value = search.Trim(), op = "WILDCARD" } };
        }
        // Click-routed filters from "by Author" / category-chip / tag-chip
        // taps on the detail view. Use EQUALS (exact match) since the
        // user explicitly clicked the value we have on hand.
        if (!string.IsNullOrWhiteSpace(author))
        {
            filter["author"] = new[] { new { value = author.Trim(), op = "EQUALS" } };
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            filter["categoryName"] = new[] { new { value = category.Trim(), op = "EQUALS" } };
        }
        if (!string.IsNullOrWhiteSpace(tag))
        {
            filter["tag"] = new[] { new { value = tag.Trim(), op = "EQUALS" } };
        }

        var variables = new Dictionary<string, object>
        {
            ["filter"] = filter,
            ["sort"]   = new[] { sortValue },
            ["offset"] = offset,
            ["count"]  = count,
        };

        var raw = await ExecuteRawAsync(ListModsQuery, variables);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        // GraphQL spec — errors land at top level as an array.
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var first = errors.EnumerateArray().FirstOrDefault();
            var msg = first.ValueKind != JsonValueKind.Undefined && first.TryGetProperty("message", out var m)
                ? m.GetString() ?? "Unknown error"
                : "Unknown error";
            throw new InvalidOperationException("Nexus GraphQL: " + msg);
        }

        var modsNode = root.GetProperty("data").GetProperty("mods");
        var nodes    = modsNode.GetProperty("nodes");

        var listings = new List<NexusModListing>(capacity: nodes.GetArrayLength());
        foreach (var n in nodes.EnumerateArray())
        {
            listings.Add(new NexusModListing
            {
                ModId            = n.GetProperty("modId").GetInt32(),
                Name             = StringOr(n, "name"),
                Summary          = StringOr(n, "summary"),
                PictureUrl       = StringOr(n, "pictureUrl"),
                Author           = StringOr(n, "author"),
                Version          = StringOr(n, "version"),
                Downloads        = IntOr(n, "downloads"),
                EndorsementCount = IntOr(n, "endorsements"),
                DomainName       = n.TryGetProperty("game", out var g) && g.TryGetProperty("domainName", out var d)
                                   ? d.GetString() ?? ""
                                   : "",
                // category in v2 is a name string directly, not an id —
                // map onto CategoryId field as 0 and stash the name in
                // the listing's "Summary" preface only if needed.
                CategoryId = 0,
            });
        }
        return new NexusModPage
        {
            Mods       = listings,
            TotalCount = modsNode.TryGetProperty("totalCount", out var t) ? t.GetInt32() : listings.Count,
            // The v2 Mod.category field is a plain string — capture it
            // per-row so the UI can show it without a separate fetch.
            Categories = nodes.EnumerateArray()
                .Select(n => StringOr(n, "category"))
                .ToList(),
        };
    }

    private static string StringOr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int IntOr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    /// <summary>
    /// Single-mod detail fetch. Returns a richer payload than the list
    /// query: the full description body, ordered tag list, and the
    /// mod requirements (Nexus + DLCs). Used when the user drills
    /// into a card on the browser page.
    /// </summary>
    public async Task<NexusModDetail> GetModAsync(int modId, int gameId)
    {
        var variables = new Dictionary<string, object>
        {
            ["modId"]  = modId.ToString(),
            ["gameId"] = gameId.ToString(),
        };
        var raw = await ExecuteRawAsync(GetModQuery, variables);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var first = errors.EnumerateArray().FirstOrDefault();
            var msg = first.ValueKind != JsonValueKind.Undefined && first.TryGetProperty("message", out var m)
                ? m.GetString() ?? "Unknown error"
                : "Unknown error";
            throw new InvalidOperationException("Nexus GraphQL: " + msg);
        }

        var mod = root.GetProperty("data").GetProperty("mod");

        var tags = new List<string>();
        if (mod.TryGetProperty("tags", out var tagArr) && tagArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tagArr.EnumerateArray())
            {
                var name = StringOr(t, "name");
                if (!string.IsNullOrWhiteSpace(name)) tags.Add(name);
            }
        }

        var reqs = new List<NexusRequirementSummary>();
        if (mod.TryGetProperty("modRequirements", out var modReqs)
            && modReqs.TryGetProperty("nexusRequirements", out var nexusReqs)
            && nexusReqs.TryGetProperty("nodes", out var reqNodes)
            && reqNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in reqNodes.EnumerateArray())
            {
                reqs.Add(new NexusRequirementSummary
                {
                    ModName             = StringOr(r, "modName"),
                    Notes               = StringOr(r, "notes"),
                    Url                 = StringOr(r, "url"),
                    ExternalRequirement = r.TryGetProperty("externalRequirement", out var ext)
                                          && ext.ValueKind == JsonValueKind.True,
                });
            }
        }

        return new NexusModDetail
        {
            ModId        = mod.GetProperty("modId").GetInt32(),
            Name         = StringOr(mod, "name"),
            Description  = StringOr(mod, "description"),
            Author       = StringOr(mod, "author"),
            Category     = StringOr(mod, "category"),
            Version      = StringOr(mod, "version"),
            DomainName   = mod.TryGetProperty("game", out var g2) && g2.TryGetProperty("domainName", out var d2)
                           ? d2.GetString() ?? ""
                           : "",
            Tags         = tags,
            Requirements = reqs,
        };
    }

    private const string GetModQuery = @"
        query Mod($modId: ID!, $gameId: ID!) {
          mod(modId: $modId, gameId: $gameId) {
            modId
            name
            description
            author
            category
            version
            game { domainName }
            tags { name }
            modRequirements {
              nexusRequirements(offset: 0, count: 30) {
                nodes {
                  id
                  modName
                  notes
                  url
                  externalRequirement
                }
              }
            }
          }
        }";

    /// <summary>
    /// The mod-list GraphQL document. Kept as a constant so changes go
    /// through one place; uses variables exclusively so the GraphQL
    /// server handles all escaping.
    /// </summary>
    private const string ListModsQuery = @"
        query Mods($filter: ModsFilter, $sort: [ModsSort!], $offset: Int, $count: Int) {
          mods(filter: $filter, sort: $sort, offset: $offset, count: $count) {
            nodes {
              modId
              name
              summary
              pictureUrl
              author
              category
              version
              downloads
              endorsements
              game { domainName }
            }
            totalCount
          }
        }";
}

/// <summary>
/// Single-mod detail payload from the v2 <c>mod()</c> query.
/// Carries the full description body plus the per-mod tag/requirements
/// data that the list query doesn't include.
/// </summary>
public sealed class NexusModDetail
{
    public int                              ModId        { get; init; }
    public string                           Name         { get; init; } = "";
    public string                           Description  { get; init; } = "";
    public string                           Author       { get; init; } = "";
    public string                           Category     { get; init; } = "";
    public string                           Version      { get; init; } = "";
    public string                           DomainName   { get; init; } = "";
    public List<string>                     Tags         { get; init; } = new();
    public List<NexusRequirementSummary>    Requirements { get; init; } = new();
}

public sealed class NexusRequirementSummary
{
    public string ModName              { get; init; } = "";
    public string Notes                { get; init; } = "";
    public string Url                  { get; init; } = "";
    public bool   ExternalRequirement  { get; init; }
}

/// <summary>
/// One page of a paginated <c>mods()</c> query. <see cref="Mods"/>
/// holds the row data; <see cref="TotalCount"/> is the server's count
/// of all matching mods across all pages — used by the UI to gate
/// further "load more" requests.
/// </summary>
public sealed class NexusModPage
{
    public List<NexusModListing> Mods       { get; init; } = new();
    public List<string>          Categories { get; init; } = new();
    public int                   TotalCount { get; init; }
}
