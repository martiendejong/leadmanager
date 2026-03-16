using System.Text;
using System.Text.Json;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public class GooglePlacesEnrichmentService
{
    private readonly HttpClient _http;
    private readonly ILogger<GooglePlacesEnrichmentService> _logger;
    private readonly string? _apiKey;

    public GooglePlacesEnrichmentService(IConfiguration configuration, ILogger<GooglePlacesEnrichmentService> logger)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
        _apiKey = configuration["GooglePlaces:ApiKey"];
    }

    public async Task<GooglePlacesResult?> EnrichAsync(string companyName, string? city = null)
    {
        // Gracefully skip if no API key configured
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("Google Places API key not configured - skipping enrichment");
            return null;
        }

        try
        {
            var searchQuery = city != null ? $"{companyName} {city}" : companyName;

            var requestBody = new
            {
                textQuery = searchQuery,
                languageCode = "nl"
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Goog-Api-Key", _apiKey);
            request.Headers.Add("X-Goog-FieldMask", "places.id,places.displayName,places.rating,places.userRatingCount,places.googleMapsUri");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Places API returned {StatusCode} for company: {CompanyName}", response.StatusCode, companyName);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<GooglePlacesResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data?.Places == null || data.Places.Count == 0) return null;

            var firstPlace = data.Places[0];
            return new GooglePlacesResult
            {
                GoogleRating = firstPlace.Rating,
                GoogleReviewCount = firstPlace.UserRatingCount,
                GoogleMapsUrl = firstPlace.GoogleMapsUri
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Places enrichment failed for company: {CompanyName}", companyName);
            return null;
        }
    }

    private record GooglePlacesResponse(List<Place>? Places);

    private record Place(
        string? Id,
        DisplayName? DisplayName,
        float? Rating,
        int? UserRatingCount,
        string? GoogleMapsUri
    );

    private record DisplayName(string? Text);
}

public record GooglePlacesResult
{
    public float? GoogleRating { get; init; }
    public int? GoogleReviewCount { get; init; }
    public string? GoogleMapsUrl { get; init; }
}
