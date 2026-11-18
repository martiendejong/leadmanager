// Jengo Track client for .NET tools. Copy this single file into a project (no package needed) or reference it as a link.
// Every call is failure-tolerant by design: a tracking outage must never block sending a mail or saving a lead.
//
//   var track = new JengoTrackClient(new HttpClient(), "https://go.prospergenics.com", apiKey);
//   var r = await track.RewriteAsync(new RewriteRequest { Html = html, EntityType = "lead", EntityId = "42",
//                                                         EntityLabel = "Hogenkamp", Campaign = "koude-acquisitie",
//                                                         SourceTool = "jengomail", Channel = "mail", MessageId = id });
//   body = r.Html;            // links replaced; original body returned unchanged when Track is unreachable
//   ... send ...
//   await track.MarkSentAsync(id);   // lets Track recognise mail-scanner clicks right after delivery
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JengoTrack;

public sealed class TrackLink
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("short_url")] public string ShortUrl { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class LinkRequest
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("entity_type")] public string? EntityType { get; set; }
    [JsonPropertyName("entity_id")] public string? EntityId { get; set; }
    [JsonPropertyName("entity_label")] public string? EntityLabel { get; set; }
    [JsonPropertyName("campaign")] public string? Campaign { get; set; }
    [JsonPropertyName("source_tool")] public string? SourceTool { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("message_id")] public string? MessageId { get; set; }
}

public sealed class RewriteRequest
{
    [JsonPropertyName("html")] public string? Html { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("entity_type")] public string? EntityType { get; set; }
    [JsonPropertyName("entity_id")] public string? EntityId { get; set; }
    [JsonPropertyName("entity_label")] public string? EntityLabel { get; set; }
    [JsonPropertyName("campaign")] public string? Campaign { get; set; }
    [JsonPropertyName("source_tool")] public string? SourceTool { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("message_id")] public string? MessageId { get; set; }
}

public sealed class RewriteResult
{
    [JsonPropertyName("html")] public string? Html { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("links")] public List<TrackLink> Links { get; set; } = new();
    /// <summary>True when Track answered; false means the original body was returned untouched.</summary>
    [JsonIgnore] public bool Tracked { get; set; }
}

/// <summary>Payload Track posts to a webhook when a human clicks.</summary>
public sealed class ClickWebhook
{
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("entity_type")] public string? EntityType { get; set; }
    [JsonPropertyName("entity_id")] public string? EntityId { get; set; }
    [JsonPropertyName("entity_label")] public string? EntityLabel { get; set; }
    [JsonPropertyName("campaign")] public string? Campaign { get; set; }
    [JsonPropertyName("source_tool")] public string? SourceTool { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("total_human_clicks")] public int? TotalHumanClicks { get; set; }

    public DateTimeOffset At => DateTimeOffset.FromUnixTimeMilliseconds(Ts);
}

public sealed class JengoTrackClient
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _key;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public JengoTrackClient(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _base = baseUrl.TrimEnd('/');
        _key = apiKey;
    }

    /// <summary>Creates one tracking link. Returns null when Track is unreachable or rejects the destination.</summary>
    public async Task<TrackLink?> CreateLinkAsync(LinkRequest req, CancellationToken ct = default)
        => await PostAsync<TrackLink>("/api/links", req, ct);

    /// <summary>Replaces every eligible link in a mail/message body. On any failure the ORIGINAL body is returned.</summary>
    public async Task<RewriteResult> RewriteAsync(RewriteRequest req, CancellationToken ct = default)
    {
        var res = await PostAsync<RewriteResult>("/api/rewrite", req, ct);
        if (res == null) return new RewriteResult { Html = req.Html, Text = req.Text, Tracked = false };
        res.Tracked = true;
        return res;
    }

    /// <summary>Call right after the message was accepted for delivery.</summary>
    public async Task MarkSentAsync(string messageId, CancellationToken ct = default)
        => await PostAsync<object>("/api/links/mark-sent", new { message_id = messageId }, ct);

    /// <summary>Stops tracking for a person and erases what was recorded (AVG request).</summary>
    public async Task OptOutAsync(string entityType, string entityId, CancellationToken ct = default)
        => await PostAsync<object>("/api/optout", new { entity_type = entityType, entity_id = entityId }, ct);

    /// <summary>Everything Track knows about one entity (for a timeline in the calling tool). Null when unreachable.</summary>
    public async Task<JsonElement?> GetTimelineAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/entities/{Uri.EscapeDataString(entityType)}/{Uri.EscapeDataString(entityId)}/timeline");
            req.Headers.Add("X-Api-Key", _key);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>Verifies the X-Track-Signature header of a webhook call (constant-time).</summary>
    public static bool VerifySignature(string secret, string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signatureHeader[7..]));
    }

    private async Task<T?> PostAsync<T>(string path, object body, CancellationToken ct) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _base + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Api-Key", _key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5)); // never hold up a send for longer than this
            using var res = await _http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            return await JsonSerializer.DeserializeAsync<T>(await res.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        }
        catch { return null; }
    }
}
