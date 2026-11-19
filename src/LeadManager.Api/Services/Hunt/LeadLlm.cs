using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeadManager.Api.Services.Hunt;

public class HuntBudgetExceededException : Exception
{
    public HuntBudgetExceededException(int max) : base($"The language model call budget of this job ({max}) is used up.") { }
}

/// <summary>A hard cap on language model calls for one job, so a hunt can never run away with the bill.</summary>
public class HuntBudget
{
    private int _used;
    public int Max { get; }
    public HuntBudget(int max) => Max = Math.Max(1, max);
    public int Used => Volatile.Read(ref _used);
    public bool HasRoom(int calls = 1) => Used + calls <= Max;
    public void Consume()
    {
        if (Interlocked.Increment(ref _used) > Max)
        {
            Interlocked.Decrement(ref _used);
            throw new HuntBudgetExceededException(Max);
        }
    }
}

public record DiscoveredCompany(string Name, string Website, string City, string Sector, string Why);

public class AiNotConfiguredException : Exception
{
    public const string Message_ = "AI key not configured on this server";
    public AiNotConfiguredException() : base(Message_) { }
}

/// <summary>The language model behind the extractor and the critic. Replaceable, so tests run without a key or a network.</summary>
public interface ILeadLlm
{
    bool IsConfigured { get; }
    string ModelName { get; }
    /// <summary>One call that answers with a JSON object (the text of that object).</summary>
    Task<string> CompleteJsonAsync(string system, string user, HuntBudget budget, CancellationToken ct);
}

/// <summary>Finds candidate companies with a real web search. The result is only a list of websites to check, never evidence.</summary>
public interface ILeadDiscovery
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<DiscoveredCompany>> DiscoverAsync(string segment, string region, IReadOnlyCollection<string> excludeDomains, int count, HuntBudget budget, CancellationToken ct);
}

public class OpenAiLeadLlm : ILeadLlm, ILeadDiscovery
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string _searchModel;
    private readonly ILogger<OpenAiLeadLlm> _logger;

    public OpenAiLeadLlm(HttpClient http, IConfiguration config, ILogger<OpenAiLeadLlm> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = config["OpenAI:ApiKey"];
        _model = string.IsNullOrWhiteSpace(config["Hunt:Model"]) ? "gpt-4.1-mini" : config["Hunt:Model"]!;
        _searchModel = string.IsNullOrWhiteSpace(config["Hunt:SearchModel"]) ? _model : config["Hunt:SearchModel"]!;
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
    public string ModelName => _model;

    private HttpRequestMessage Post(string url, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        return req;
    }

    public async Task<string> CompleteJsonAsync(string system, string user, HuntBudget budget, CancellationToken ct)
    {
        if (!IsConfigured) throw new AiNotConfiguredException();
        budget.Consume();
        using var req = Post("https://api.openai.com/v1/chat/completions", new
        {
            model = _model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        });
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI chat call failed with {Status}", (int)resp.StatusCode); // never log the body: it may echo page text
            throw new HttpRequestException($"The language model call failed with HTTP {(int)resp.StatusCode}.");
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
    }

    public async Task<IReadOnlyList<DiscoveredCompany>> DiscoverAsync(string segment, string region, IReadOnlyCollection<string> excludeDomains, int count, HuntBudget budget, CancellationToken ct)
    {
        if (!IsConfigured) throw new AiNotConfiguredException();
        budget.Consume();

        var exclude = excludeDomains.Count == 0 ? "none" : string.Join(", ", excludeDomains.Take(150));
        var prompt =
            "Use web search to find real, currently operating Dutch businesses for a sales researcher.\n" +
            $"Segment: {segment}\nRegion: {(string.IsNullOrWhiteSpace(region) ? "anywhere in the Netherlands" : region)}\n" +
            $"Return up to {count} companies.\n\n" +
            "Rules:\n" +
            "- Prefer small independent businesses where one named owner runs the business and the business has its OWN website.\n" +
            "- Give the exact address of the business's own website (home page), never a directory, a social page or a review site.\n" +
            "- Only companies that appear in your search results. Never invent a company or a website address.\n" +
            "- Do not return contact details; only name, website, city and a short reason.\n" +
            $"- Do not return these domains: {exclude}\n\n" +
            "Answer with JSON only: {\"companies\":[{\"name\":\"\",\"website\":\"https://...\",\"city\":\"\",\"sector\":\"\",\"why\":\"\"}]}";

        using var req = Post("https://api.openai.com/v1/responses", new
        {
            model = _searchModel,
            tools = new object[] { new { type = "web_search_preview" } },
            input = prompt,
        });
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI web search call failed with {Status}", (int)resp.StatusCode);
            throw new HttpRequestException($"The web search call failed with HTTP {(int)resp.StatusCode}.");
        }

        var text = new StringBuilder();
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("output", out var output))
                foreach (var item in output.EnumerateArray())
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "message" && item.TryGetProperty("content", out var content))
                        foreach (var part in content.EnumerateArray())
                            if (part.TryGetProperty("text", out var tx)) text.Append(tx.GetString());
        }
        return ParseCompanies(text.ToString());
    }

    public static IReadOnlyList<DiscoveredCompany> ParseCompanies(string text)
    {
        var list = new List<DiscoveredCompany>();
        var m = Regex.Match(text ?? "", @"\{[\s\S]*\}");
        if (!m.Success) return list;
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            if (!doc.RootElement.TryGetProperty("companies", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in arr.EnumerateArray())
            {
                string S(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var name = S("name").Trim();
                var site = S("website").Trim();
                if (name.Length == 0 || !UrlGuard.TryNormalize(site, out var uri)) continue;
                list.Add(new DiscoveredCompany(name, $"{uri.Scheme}://{uri.Host}/", S("city").Trim(), S("sector").Trim(), S("why").Trim()));
            }
        }
        catch (JsonException) { }
        return list;
    }
}
