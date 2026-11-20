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
    public const string Text = "Agent hunting is not configured on this server";
    public DispatchNotConfiguredException() : base(Text) { }
}

/// <summary>The request may have reached JengoWork (a timeout, a dropped connection, a 5xx, an answer without a task id): a task may exist even though we got no answer.</summary>
public class DispatchUncertainException : Exception
{
    public DispatchUncertainException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Starts the background Jengo agent that does the research of one hunt. Replaceable, so tests run without a network.</summary>
public interface IHuntDispatcher
{
    bool IsConfigured { get; }
    /// <summary>
    /// Creates the task. Throws <see cref="DispatchUncertainException"/> when it cannot tell whether the task was created, and any other exception
    /// when it is sure that it was not.
    /// </summary>
    Task<DispatchResult> DispatchAsync(HuntJob job, string prompt, CancellationToken ct);
    /// <summary>The task of this job when JengoWork has one (used after an uncertain dispatch), or null.</summary>
    Task<DispatchResult?> FindAsync(HuntJob job, CancellationToken ct);
}

/// <summary>
/// Dispatches by creating a JengoWork task on the configured board. HOW THE TASK IS PICKED UP (checked on board 105, 20-11-2026): a task created
/// through the API lands in the lane "prioritization backlog", an automatic lane of the standard pipeline, and a task created INTO an automatic lane
/// fires no handler; board 105 is also not monitored by jengo-agi (agiMonitored = false), so its poll loop does not see it either. Until the board is
/// monitored and a lane that starts an agent is set up, a PERSON has to start the task (move it to todo, or run it by hand). The app says so
/// (Hunt:Dispatch:AgentPicksUp, default false, switches the texts) and ends a hunt that nobody started after <see cref="HuntSweepService.PendingGrace"/>.
/// The task text contains the TASK token OF THIS HUNT (random, stored hashed, see <see cref="HuntTokens"/>): it is good for one hour and for one
/// call, /brief, which exchanges it for a session token; a copy of the task text opens nothing afterwards. It never contains a JengoWork key.
/// </summary>
public class JengoWorkHuntDispatcher : IHuntDispatcher
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _boardId;
    private readonly string _status;

    /// <summary>The board "Jengo Clients" (publicId zKaEpzhJGG) in JengoWork.</summary>
    public const string DefaultBoardId = "105";

    public JengoWorkHuntDispatcher(HttpClient http, IConfiguration config)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(20);
        _baseUrl = (config["JengoWork:BaseUrl"] ?? "").Trim().TrimEnd('/');
        _apiKey = (config["JengoWork:ApiKey"] ?? "").Trim();
        _boardId = (config["Hunt:Dispatch:BoardId"] is { Length: > 0 } b ? b : DefaultBoardId).Trim();
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
        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(req, ct);
            text = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DispatchUncertainException("JengoWork did not answer in time when the agent task was created.");   // the timeout of the client
        }
        catch (HttpRequestException ex)
        {
            throw new DispatchUncertainException("The connection to JengoWork failed when the agent task was created.", ex);
        }
        using (resp)
        {
            // A 4xx is a refusal: nothing was created. A 5xx says nothing about whether the task was created before it failed.
            if ((int)resp.StatusCode >= 500) throw new DispatchUncertainException($"JengoWork answered {(int)resp.StatusCode} when the agent task was created.");
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"JengoWork answered {(int)resp.StatusCode} when the agent task was created.");
        }
        var created = ReadTask(text);
        if (created == null) throw new DispatchUncertainException("JengoWork created the task but returned no task id.");
        return created;
    }

    private static DispatchResult? ReadTask(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? id = null, url = null;
        if (root.TryGetProperty("id", out var i)) id = i.ValueKind == JsonValueKind.Number ? i.GetRawText() : i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) url = u.GetString();
        if (string.IsNullOrWhiteSpace(id)) return null;
        // Only an http(s) address is kept as the task link: it is rendered as a link in the app.
        if (url != null && !(Uri.TryCreate(url, UriKind.Absolute, out var taskUri) && (taskUri.Scheme == Uri.UriSchemeHttp || taskUri.Scheme == Uri.UriSchemeHttps))) url = null;
        return new DispatchResult(id!, url);
    }

    private static DispatchResult? ReadTask(string text)
    {
        try { using var doc = JsonDocument.Parse(text); return ReadTask(doc.RootElement); }
        catch (JsonException) { return null; }
    }

    /// <summary>Looks for the task of this job in the newest tasks of the board (its title carries the first characters of the job id).</summary>
    public async Task<DispatchResult?> FindAsync(HuntJob job, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v2/list/{_boardId}/task");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tasks", out var t) ? t : default;
        if (list.ValueKind != JsonValueKind.Array) return null;
        var title = HuntPrompt.TaskTitle(job);
        foreach (var task in list.EnumerateArray().Take(100))
            if (task.ValueKind == JsonValueKind.Object && task.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == title)
                return ReadTask(task);
        return null;
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
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("AGENT_PROMPT.md", StringComparison.Ordinal));
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

    public static string TaskTitle(HuntJob job) => job.Kind == HuntKinds.Review
        ? $"Lead review {job.Id.ToString()[..8]}: {job.LeadIds.Count} lead(s)"
        : $"Lead hunt {job.Id.ToString()[..8]}: {Sanitize(job.Segment, 60)}";

    public static string Render(HuntJob job, string apiBase, string agentToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            mode = job.Kind == HuntKinds.Review ? "review" : "hunt",
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
            .Replace("{{TOKEN_LIFETIME_MINUTES}}", ((int)HuntTokens.Lifetime.TotalMinutes).ToString())
            .Replace("{{SESSION_IDLE_MINUTES}}", ((int)HuntTokens.SessionIdle.TotalMinutes).ToString())
            .Replace("{{SILENCE_MINUTES}}", ((int)HuntSweepService.Silence.TotalMinutes).ToString())
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
