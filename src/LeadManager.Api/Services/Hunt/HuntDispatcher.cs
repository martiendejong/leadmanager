using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Hunt;

public record DispatchResult(string TaskId, string? TaskUrl);

public class DispatchNotConfiguredException : Exception
{
    public const string Text = "The Jengo agent dispatch is not configured on this server";
    public DispatchNotConfiguredException() : base(Text) { }
}

/// <summary>Starts the background Jengo agent that does the research of one hunt. Replaceable, so tests run without a network.</summary>
public interface IHuntDispatcher
{
    bool IsConfigured { get; }
    Task<DispatchResult> DispatchAsync(HuntJob job, string prompt, CancellationToken ct);
}

/// <summary>
/// Dispatches by creating a JengoWork task on the configured board: jengo-agi picks the task up and spawns an agent with the task
/// text as its prompt. The task text contains the token OF THIS HUNT (an HMAC of the hunt id, see <see cref="HuntTokens"/>), and that is acceptable
/// by design: the token opens only this hunt's agent endpoints, only while the hunt is Pending or Running, so a copy of the task text is
/// worthless once the hunt ends and for every other hunt. It never contains the server secret, a JengoWork key or any other credential.
/// </summary>
public class JengoWorkHuntDispatcher : IHuntDispatcher
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _boardId;
    private readonly string _status;

    public JengoWorkHuntDispatcher(HttpClient http, IConfiguration config)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _baseUrl = (config["JengoWork:BaseUrl"] ?? "").Trim().TrimEnd('/');
        _apiKey = (config["JengoWork:ApiKey"] ?? "").Trim();
        _boardId = (config["Hunt:Dispatch:BoardId"] ?? "").Trim();
        _status = (config["Hunt:Dispatch:Status"] ?? "").Trim();
    }

    public bool IsConfigured => _baseUrl != "" && _apiKey != "" && HuntRegex.IsMatch(_boardId, @"^\d{1,9}$") && Uri.IsWellFormedUriString(_baseUrl, UriKind.Absolute);

    public async Task<DispatchResult> DispatchAsync(HuntJob job, string prompt, CancellationToken ct)
    {
        if (!IsConfigured) throw new DispatchNotConfiguredException();

        var body = new Dictionary<string, object?> { ["name"] = HuntPrompt.TaskTitle(job), ["description"] = prompt };
        if (_status != "") body["status"] = _status;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/v2/list/{_boardId}/task")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"JengoWork answered {(int)resp.StatusCode} when the agent task was created.");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string? id = null, url = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("id", out var i)) id = i.ValueKind == JsonValueKind.Number ? i.GetRawText() : i.GetString();
            if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) url = u.GetString();
        }
        if (string.IsNullOrWhiteSpace(id)) throw new HttpRequestException("JengoWork created the task but returned no task id.");
        // Only an http(s) address is kept as the task link: it is rendered as a link in the app.
        if (url != null && !(Uri.TryCreate(url, UriKind.Absolute, out var taskUri) && (taskUri.Scheme == Uri.UriSchemeHttp || taskUri.Scheme == Uri.UriSchemeHttps))) url = null;
        return new DispatchResult(id!, url);
    }
}

/// <summary>Renders the versioned agent prompt (an embedded resource) with the sanitized criteria of one hunt.</summary>
public static class HuntPrompt
{
    private static string? _template;

    public static string Template()
    {
        if (_template != null) return _template;
        var asm = typeof(HuntPrompt).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("LEAD_HUNT_PROMPT.md", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return _template = reader.ReadToEnd();
    }

    /// <summary>Letters, digits, spaces and a few punctuation marks only: no control characters, markup or quotes can reach the prompt.</summary>
    public static string Sanitize(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var kept = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch)) kept.Append(' ');
            else if (char.IsLetterOrDigit(ch) || ch is '-' or ',' or '.' or '&' or '/' or '(' or ')' or '\'') kept.Append(ch);
            if (kept.Length >= max) break;
        }
        return HuntRegex.Replace(kept.ToString(), @"\s+", " ").Trim();
    }

    public static string TaskTitle(HuntJob job) => $"Lead hunt {job.Id.ToString()[..8]}: {Sanitize(job.Segment, 60)}";

    public static string Render(HuntJob job, string apiBase, string agentToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            huntId = job.Id,
            segment = job.Segment,
            region = job.Region,
            minScore = job.MinScore,
            targetCount = job.TargetCount,
            maxCandidates = job.MaxCandidates,
        });
        return Template()
            .Replace("{{DATA}}", data)
            .Replace("{{HUNT_ID}}", job.Id.ToString())
            .Replace("{{API_BASE}}", apiBase.TrimEnd('/'))
            .Replace("{{AGENT_TOKEN}}", agentToken)
            .Replace("{{PRODUCTS}}", ProductCatalog.PublicText);
    }
}

/// <summary>Who may start a hunt: the configured users (Hunt:AllowedEmails, comma separated) or, when none are configured, the admins.</summary>
public static class HuntAccess
{
    public static async Task<bool> IsAllowedAsync(LeadManagerDbContext db, IConfiguration config, string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;
        var allowed = (config["Hunt:AllowedEmails"] ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length > 0)
        {
            var email = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();
            return email != null && allowed.Contains(email, StringComparer.OrdinalIgnoreCase);
        }
        return await (from ur in db.UserRoles
                      join r in db.Roles on ur.RoleId equals r.Id
                      where ur.UserId == userId && r.Name == "Admin"
                      select ur.UserId).AnyAsync();
    }

    public static string ApiBase(IConfiguration config) =>
        (config["Hunt:ApiBaseUrl"] is { Length: > 0 } b ? b : config["Sso:CallbackBaseUrl"] ?? "").TrimEnd('/');
}
