using System.Text.Json;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public class KvkEnrichmentService
{
    private readonly HttpClient _http;
    private readonly ILogger<KvkEnrichmentService> _logger;

    public KvkEnrichmentService(ILogger<KvkEnrichmentService> logger)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
    }

    public async Task<KvkEnrichmentResult?> EnrichAsync(string companyName, string? city = null)
    {
        try
        {
            // Try with city first if available
            if (!string.IsNullOrWhiteSpace(city))
            {
                var resultWithCity = await FetchFromApiAsync(companyName, city);
                if (resultWithCity != null) return resultWithCity;
            }

            // Fallback: try without city
            return await FetchFromApiAsync(companyName, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KvK enrichment failed for company: {CompanyName}", companyName);
            return null;
        }
    }

    private async Task<KvkEnrichmentResult?> FetchFromApiAsync(string companyName, string? city)
    {
        var encodedName = Uri.EscapeDataString(companyName);
        var url = city != null
            ? $"https://api.openkvk.nl/json/{encodedName}/{Uri.EscapeDataString(city)}/1/"
            : $"https://api.openkvk.nl/json/{encodedName}/1/";

        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<OpenKvkResponse>(json);

        if (data?.Results == null || data.Results.Count == 0) return null;

        var firstResult = data.Results[0];
        return new KvkEnrichmentResult
        {
            KvkNumber = firstResult.KvkNummer,
            VatNumber = firstResult.BtwNummer,
            Street = firstResult.Straat,
            ZipCode = firstResult.Postcode,
            EmployeeCount = firstResult.AantalWerknemers,
            FoundingYear = ParseFoundingYear(firstResult.Oprichtingsdatum),
            LegalForm = firstResult.Rechtsvorm
        };
    }

    private static int? ParseFoundingYear(string? dateString)
    {
        if (string.IsNullOrWhiteSpace(dateString)) return null;
        if (DateTime.TryParse(dateString, out var date)) return date.Year;
        if (int.TryParse(dateString[..4], out var year)) return year;
        return null;
    }

    private record OpenKvkResponse(List<OpenKvkResult>? Results);

    private record OpenKvkResult(
        string? KvkNummer,
        string? BtwNummer,
        string? Straat,
        string? Postcode,
        string? AantalWerknemers,
        string? Oprichtingsdatum,
        string? Rechtsvorm
    );
}

public record KvkEnrichmentResult
{
    public string? KvkNumber { get; init; }
    public string? VatNumber { get; init; }
    public string? Street { get; init; }
    public string? ZipCode { get; init; }
    public string? EmployeeCount { get; init; }
    public int? FoundingYear { get; init; }
    public string? LegalForm { get; init; }
}
