using LeadManager.Api.Models;
using System.Text.Json;

namespace LeadManager.Api.Services;

public class WorkQueueItem
{
    public string? Id { get; set; }
    public string? Status { get; set; }
}

public class MinoxPocConnectorService
{
    private readonly IConfiguration _config;
    private readonly ILogger<MinoxPocConnectorService> _logger;
    private readonly HttpClient _http;

    public MinoxPocConnectorService(IConfiguration config, ILogger<MinoxPocConnectorService> logger, HttpClient http)
    {
        _config = config;
        _logger = logger;
        _http = http;
    }

    public async Task<string?> CreateWorkItemAsync(ClientIntake intake, string clientName)
    {
        var baseUrl = _config["MinoxPoc:BaseUrl"] ?? "http://localhost:3001";

        try
        {
            var payload = new
            {
                type = MapProductType(intake.ProductType),
                title = $"{clientName} — {intake.FirstTask ?? intake.Requirements[..Math.Min(60, intake.Requirements.Length)]}",
                description = intake.Requirements,
                priority = "normal",
                customer = new { id = intake.ClientId.ToString(), name = clientName },
                context = new
                {
                    productType = intake.ProductType.ToString(),
                    bundleType = intake.BundleType.ToString(),
                    estimatedHours = intake.EstimatedHours,
                    estimatedPrice = intake.EstimatedPrice,
                    additionalNotes = intake.AdditionalNotes,
                    intakeId = intake.Id.ToString()
                }
            };

            var response = await _http.PostAsJsonAsync($"{baseUrl}/api/work-queue/items", payload);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            }

            _logger.LogWarning("MinoxPoc returned {Status} when creating work item", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create work item in minox-poc");
            return null;
        }
    }

    public async Task<WorkQueueItem?> GetWorkItemAsync(string workItemId)
    {
        var baseUrl = _config["MinoxPoc:BaseUrl"] ?? "http://localhost:3001";

        try
        {
            var response = await _http.GetAsync($"{baseUrl}/api/work-queue/items/{workItemId}");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<WorkQueueItem>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get work item {Id} from minox-poc", workItemId);
            return null;
        }
    }

    private static string MapProductType(IntakeProductType type) => type switch
    {
        IntakeProductType.Website => "website-project",
        IntakeProductType.AIEmployee => "ai-employee",
        IntakeProductType.AITeam => "ai-team",
        IntakeProductType.Custom => "custom-project",
        _ => "general"
    };
}
