using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MgsvModMgr.Core;

/// <summary>
/// Thin HttpClient wrapper around the Nexus Mods v1 REST API.
/// Auth via the user's Personal API key (header: <c>apikey: ...</c>);
/// game scope is hard-pinned to <c>metalgearsolidvtpp</c>.
/// All endpoints used are read-only (browse / fetch metadata + images);
/// downloads go through the OS-wide <c>nxm://</c> handler — not here.
/// </summary>
public sealed class NexusClient
{
    private const string ApiBase    = "https://api.nexusmods.com/v1";
    public  const string GameDomain = "metalgearsolidvtpp";

    /// <summary>
    /// Process-shared HttpClient. .NET pools connections per host on
    /// this instance, so reusing it across all API + image fetches
    /// (vs new-per-call) keeps the trending+12-thumbnails open sequence
    /// down to one or two TCP handshakes instead of fourteen.
    /// </summary>
    private static readonly HttpClient _http = MakeHttpClient();

    private readonly string _apiKey;
    public NexusClient(string apiKey) { _apiKey = apiKey; }

    private static HttpClient MakeHttpClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("mgsv_modmgr/1.0 (+https://github.com)");
        return h;
    }

    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, ApiBase + path);
        r.Headers.Add("apikey", _apiKey);
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return r;
    }

    /// <summary>Cheap "is this key any good?" check. Used after Settings save.</summary>
    public async Task<bool> ValidateAsync()
    {
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/users/validate.json"));
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Fetch a CDN-hosted mod thumbnail. picture_url is public, no
    /// apikey header needed — but we share the same HttpClient for
    /// connection reuse, so it lives here.
    /// </summary>
    public Task<byte[]> FetchImageBytesAsync(string url)
        => _http.GetByteArrayAsync(url);

    /// <summary>
    /// Exchange the nxm:// token for a real (one-time, short-lived)
    /// CDN download URL via the v1 API. Free users land here via the
    /// website's "Mod Manager Download" button which packs a temporary
    /// signed key into the nxm URL; Premium users could call without
    /// the key/expires query params, but for unified handling we
    /// always pass them when present.
    /// </summary>
    public async Task<string> GetDownloadLinkAsync(int modId, int fileId, string? key, long? expires)
    {
        var path = $"/games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json";
        if (!string.IsNullOrEmpty(key) && expires.HasValue)
            path += $"?key={Uri.EscapeDataString(key)}&expires={expires.Value}";

        using var resp = await _http.SendAsync(Req(HttpMethod.Get, path));
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var arr  = JsonSerializer.Deserialize<JsonElement>(json);
        // Response is an array of CDN mirrors; pick the first.
        if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        {
            var first = arr[0];
            if (first.TryGetProperty("URI", out var uri) && uri.ValueKind == JsonValueKind.String)
                return uri.GetString() ?? "";
        }
        throw new InvalidOperationException("Nexus returned no download URL.");
    }

    /// <summary>
    /// Stream a CDN download into the destination file path. Caller
    /// owns the file lifecycle (we don't move/delete on failure).
    /// </summary>
    public async Task DownloadFileAsync(string url, string destPath, IProgress<long>? progress = null)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            await dst.WriteAsync(buf, 0, n);
            total += n;
            progress?.Report(total);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// Minimal projection of the Nexus Mods v1 mod-listing JSON object.
/// Only the fields the browser page actually displays are mapped;
/// everything else (timestamps / adult-content flags / uploader ids
/// etc.) is ignored at deserialise time.
/// </summary>
public sealed class NexusModListing
{
    [JsonPropertyName("mod_id")]            public int    ModId            { get; set; }
    [JsonPropertyName("name")]              public string Name             { get; set; } = "";
    [JsonPropertyName("summary")]           public string Summary          { get; set; } = "";
    [JsonPropertyName("picture_url")]       public string PictureUrl       { get; set; } = "";
    [JsonPropertyName("mod_downloads")]     public int    Downloads        { get; set; }
    [JsonPropertyName("endorsement_count")] public int    EndorsementCount { get; set; }
    [JsonPropertyName("author")]            public string Author           { get; set; } = "";
    [JsonPropertyName("version")]           public string Version          { get; set; } = "";
    [JsonPropertyName("domain_name")]       public string DomainName       { get; set; } = "";
    /// <summary>v2 GraphQL pulls this; v1 REST endpoints don't return it.</summary>
    public int GameId { get; set; }
    [JsonPropertyName("category_id")]       public int    CategoryId       { get; set; }
}
