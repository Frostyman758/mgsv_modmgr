using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MgsvModMgr.Core;

/// <summary>
/// Nexus Mods Single Sign-On client. Replaces the manual
/// "paste your Personal API key" flow with a browser-based approval
/// flow: we open a WebSocket to sso.nexusmods.com, point the user's
/// browser at the approval page, and receive an API key back when
/// they click Approve.
///
/// <para>Wire protocol (protocol version 2):</para>
/// <list type="number">
///   <item>WS connect to wss://sso.nexusmods.com/</item>
///   <item>Send <c>{ id, token: null, protocol: 2 }</c> on first connect.</item>
///   <item>Receive <c>{ success, data: { connection_token } }</c>.</item>
///   <item>Open browser to <c>https://www.nexusmods.com/sso?id={id}&amp;application={slug}</c>.</item>
///   <item>User logs into Nexus + clicks Approve.</item>
///   <item>Receive <c>{ success, data: { api_key } }</c>.</item>
/// </list>
/// </summary>
public static class NexusSso
{
    private const string SsoUrl = "wss://sso.nexusmods.com/";

    /// <summary>
    /// Application slug surfaced to the user on Nexus's approval page
    /// ("Allow X to access your account?"). Until our app is formally
    /// registered with Nexus this is a self-chosen identifier; the
    /// approval page may show it as "unknown application" depending
    /// on their side's whitelist behaviour.
    /// </summary>
    public const string AppSlug = "mgsvmodmgr";

    /// <summary>
    /// Run the full SSO flow. <paramref name="openBrowser"/> is called
    /// once with the approval URL; the caller is responsible for
    /// shelling out to xdg-open / open / ShellExecute. Returns the
    /// api_key string, or throws on protocol error / timeout / cancel.
    /// </summary>
    public static async Task<string> AuthenticateAsync(
        Action<string> openBrowser,
        CancellationToken ct = default)
    {
        var clientId = Guid.NewGuid().ToString();
        using var ws = new ClientWebSocket();
        // Overall ceiling on the whole flow — covers slow browser
        // opens + user taking a beat to log in and click Approve.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        await ws.ConnectAsync(new Uri(SsoUrl), linked.Token);

        // 1. Initial handshake. Empty token = "give me a new connection".
        await SendJsonAsync(ws, new SsoRequest { Id = clientId, Token = null, Protocol = 2 }, linked.Token);

        // 2. First response carries the connection token; we don't
        //    actually need to round-trip it for the basic flow, but
        //    we wait for it so we know the server accepted us before
        //    sending the user to the approval page.
        var ack = await ReceiveJsonAsync(ws, linked.Token);
        if (!ack.TryGetProperty("success", out var s1) || s1.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Nexus SSO server rejected the initial handshake.");

        // 3. Punt the user to the approval page in their default browser.
        var approveUrl = $"https://www.nexusmods.com/sso?id={Uri.EscapeDataString(clientId)}&application={AppSlug}";
        openBrowser(approveUrl);

        // 4. Wait for the approval message carrying the api_key.
        var approval = await ReceiveJsonAsync(ws, linked.Token);
        if (approval.TryGetProperty("success", out var s2) && s2.ValueKind == JsonValueKind.False)
        {
            var err = approval.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            throw new InvalidOperationException($"Nexus SSO refused approval. {err}");
        }
        if (!approval.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("api_key", out var key) ||
            key.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Nexus SSO didn't return an API key.");
        }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { /* ignore — we already have what we came for */ }
        return key.GetString() ?? throw new InvalidOperationException("Empty SSO api_key.");
    }

    private sealed class SsoRequest
    {
        [JsonPropertyName("id")]       public string  Id       { get; set; } = "";
        [JsonPropertyName("token")]    public string? Token    { get; set; }
        [JsonPropertyName("protocol")] public int     Protocol { get; set; }
    }

    private static async Task SendJsonAsync<T>(ClientWebSocket ws, T payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Nexus SSO connection closed before approval.");
            ms.Write(buf, 0, result.Count);
        }
        while (!result.EndOfMessage);
        ms.Position = 0;
        return await JsonSerializer.DeserializeAsync<JsonElement>(ms, cancellationToken: ct);
    }
}
