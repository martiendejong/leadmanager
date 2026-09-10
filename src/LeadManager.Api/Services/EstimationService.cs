using LeadManager.Api.Models;
using System.Text.Json;

namespace LeadManager.Api.Services;

public class EstimationResult
{
    public decimal EstimatedHours { get; set; }
    public decimal EstimatedPrice { get; set; }
    public string Reasoning { get; set; } = "";
}

public class EstimationService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EstimationService> _logger;
    private readonly HttpClient _http;

    // Hourly rates per bundle type
    private static readonly Dictionary<IntakeBundleType, decimal> HourlyRates = new()
    {
        [IntakeBundleType.Starter] = 2.50m,
        [IntakeBundleType.Team] = 2.50m,
        [IntakeBundleType.PayPerHour] = 3.00m
    };

    public EstimationService(IConfiguration config, ILogger<EstimationService> logger, HttpClient http)
    {
        _config = config;
        _logger = logger;
        _http = http;
    }

    public async Task<EstimationResult> EstimateAsync(ClientIntake intake)
    {
        try
        {
            var apiKey = _config["OpenAI:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
                return FallbackEstimate(intake);

            var prompt = BuildEstimationPrompt(intake);
            var result = await CallOpenAiAsync(apiKey, prompt);
            var rate = HourlyRates[intake.BundleType];
            result.EstimatedPrice = Math.Round(result.EstimatedHours * rate, 2);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI estimation failed, using fallback");
            return FallbackEstimate(intake);
        }
    }

    private string BuildEstimationPrompt(ClientIntake intake) =>
        $"You are a senior developer estimating hours for a client task.\n\n" +
        $"Product type: {intake.ProductType}\n" +
        $"Bundle: {intake.BundleType}\n" +
        $"Requirements: {intake.Requirements}\n" +
        $"First task: {intake.FirstTask ?? "Not specified"}\n" +
        $"Additional notes: {intake.AdditionalNotes ?? "None"}\n\n" +
        "Respond with JSON only, no markdown:\n" +
        "{\n  \"estimatedHours\": <number>,\n  \"reasoning\": \"<2-3 sentence explanation>\"\n}\n\n" +
        "Be conservative (better to under-promise). Round to nearest 0.5 hour.";

    private async Task<EstimationResult> CallOpenAiAsync(string apiKey, string prompt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = JsonContent.Create(new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.2
        });

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        using var resultDoc = JsonDocument.Parse(content);
        var hours = resultDoc.RootElement.GetProperty("estimatedHours").GetDecimal();
        var reasoning = resultDoc.RootElement.GetProperty("reasoning").GetString() ?? "";

        return new EstimationResult { EstimatedHours = hours, Reasoning = reasoning };
    }

    private EstimationResult FallbackEstimate(ClientIntake intake)
    {
        // Simple rule-based fallback
        var baseHours = intake.ProductType switch
        {
            IntakeProductType.Website => 8m,
            IntakeProductType.AIEmployee => 4m,
            IntakeProductType.AITeam => 12m,
            IntakeProductType.Custom => 6m,
            _ => 5m
        };

        // Scale by requirements length as a rough proxy
        var lengthFactor = intake.Requirements.Length > 500 ? 1.5m : 1.0m;
        var hours = Math.Round(baseHours * lengthFactor * 2) / 2; // round to 0.5
        var rate = HourlyRates[intake.BundleType];

        return new EstimationResult
        {
            EstimatedHours = hours,
            EstimatedPrice = hours * rate,
            Reasoning = $"Schatting op basis van producttype {intake.ProductType} en omvang van de requirements."
        };
    }
}
