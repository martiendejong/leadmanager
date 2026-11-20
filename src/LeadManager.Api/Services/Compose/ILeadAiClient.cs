using System.Net.Http.Json;
using System.Text.Json;

namespace LeadManager.Api.Services.Compose;

/// <summary>The AI is not configured on this server (no key). Templates keep working; only the AI features answer with this.</summary>
public class AiNotConfiguredException : Exception
{
    public AiNotConfiguredException() : base("AI is not configured on this server.") { }
}

/// <summary>The AI service did not answer in time, refused, or answered something unusable. The message is safe to show.</summary>
public class AiUnavailableException : Exception
{
    public AiUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The one place that talks to a language model. Everything else is testable with a fake of this interface.</summary>
public interface ILeadAiClient
{
    bool IsConfigured { get; }
    /// <summary>The model id that is used (for display and for the stored demo ideas).</summary>
    string Model { get; }
    /// <summary>A system prompt and a user prompt in, one JSON object (as text) out.</summary>
    Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct);
}

/// <summary>
/// OpenAI chat completions. Key: OpenAI:ApiKey (env OPENAI_API_KEY via OpenAI__ApiKey). Model: AiCompose:Model (env AI_COMPOSE_MODEL),
/// default <see cref="DefaultModel"/>. The key is never logged and never part of an error message.
/// </summary>
public class OpenAiLeadAiClient : ILeadAiClient
{
    /// <summary>A current id from jengo-knowledge-private/models/CURRENT_MODELS.md (OpenAI, small tier).</summary>
    public const string DefaultModel = "gpt-5.4-mini";
    public const int MaxOutputTokens = 4000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ILogger<OpenAiLeadAiClient> _logger;
    private readonly string _key;
    private readonly string _reasoningEffort;

    public OpenAiLeadAiClient(HttpClient http, IConfiguration config, ILogger<OpenAiLeadAiClient> logger)
    {
        _http = http;
        _http.Timeout = Timeout;
        _logger = logger;
        _key = (config["OpenAI:ApiKey"] ?? "").Trim();
        var m = (config["AiCompose:Model"] ?? "").Trim();
        Model = m.Length > 0 && m.Length <= 80 ? m : DefaultModel;
        _reasoningEffort = (config["AiCompose:ReasoningEffort"] ?? "").Trim();
    }

    public bool IsConfigured => _key.Length > 0;
    public string Model { get; }

    public async Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct)
    {
        if (!IsConfigured) throw new AiNotConfiguredException();
        var body = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            ["response_format"] = new { type = "json_object" },
            ["max_completion_tokens"] = MaxOutputTokens,
        };
        if (_reasoningEffort.Length > 0) body["reasoning_effort"] = _reasoningEffort;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Only the status and the provider's error code are logged: never the request (facts of a lead) and never a header.
                var code = "";
                try { using var err = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); code = err.RootElement.GetProperty("error").GetProperty("code").GetString() ?? ""; } catch { /* no code */ }
                _logger.LogWarning("AI compose: the model {Model} answered HTTP {Status} {Code}", Model, (int)response.StatusCode, code);
                throw new AiUnavailableException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "The AI service is busy or over its limit. Try again in a minute."
                    : "The AI service refused the request. Try again, and tell an administrator if it keeps happening.");
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var choice = doc.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.GetString() == "length")
                throw new AiUnavailableException("The AI answer was cut off. Try again with a shorter instruction.");
            var text = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException("The AI service gave an empty answer. Try again.");
            return text;
        }
        catch (AiUnavailableException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("AI compose: the model {Model} did not answer within {Seconds}s", Model, (int)Timeout.TotalSeconds);
            throw new AiUnavailableException("The AI service did not answer in time. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning("AI compose: the call failed ({Type})", ex.GetType().Name);
            throw new AiUnavailableException("The AI service could not be reached. Try again.", ex);
        }
    }
}
