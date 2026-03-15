using LeadManager.Api.Models;
using WebSearch;
using WebSearch.Core;
using WebSearch.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace LeadManager.Api.Services.Enrichment;

/// <summary>
/// Enriches leads using web search to find company information, social profiles, and relevant content.
/// Integrates the WebSearch library (https://github.com/martiendejong/websearch) for multi-engine search.
/// </summary>
public class WebSearchEnrichmentService
{
    private readonly SearchProviderFactory _searchFactory;
    private readonly ILogger<WebSearchEnrichmentService> _logger;

    public WebSearchEnrichmentService(ILogger<WebSearchEnrichmentService> logger)
    {
        _logger = logger;

        // Initialize WebSearch with caching and rate limiting
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new InMemorySearchCache(memoryCache);
        var rateLimiter = new TokenBucketRateLimiter(requestsPerMinute: 30);
        _searchFactory = new SearchProviderFactory(cache, rateLimiter);
    }

    /// <summary>
    /// Enriches a lead by searching for company information, website, and social profiles.
    /// </summary>
    public async Task<LeadEnrichmentResult> EnrichLeadAsync(Lead lead, CancellationToken cancellationToken = default)
    {
        var result = new LeadEnrichmentResult
        {
            LeadId = lead.Id,
            SearchResults = new List<SearchResultInfo>()
        };

        try
        {
            // Strategy 1: Search for company name + sector
            if (!string.IsNullOrWhiteSpace(lead.Name))
            {
                var companyResults = await SearchCompanyInfoAsync(
                    lead.Name,
                    lead.Sector,
                    cancellationToken
                );
                result.SearchResults.AddRange(companyResults);
            }

            // Strategy 2: Search for contact name + company
            if (!string.IsNullOrWhiteSpace(lead.OwnerName) && !string.IsNullOrWhiteSpace(lead.Name))
            {
                var contactResults = await SearchContactInfoAsync(
                    lead.OwnerName,
                    lead.Name,
                    cancellationToken
                );
                result.SearchResults.AddRange(contactResults);
            }

            // Strategy 3: Search for domain/website
            if (!string.IsNullOrWhiteSpace(lead.Website))
            {
                var domainResults = await SearchDomainInfoAsync(
                    lead.Website,
                    cancellationToken
                );
                result.SearchResults.AddRange(domainResults);
            }

            result.Success = true;
            result.TotalResults = result.SearchResults.Count;

            _logger.LogInformation(
                "Enriched lead {LeadId} ({CompanyName}) - Found {ResultCount} results",
                lead.Id, lead.Name, result.TotalResults
            );
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;

            _logger.LogError(ex,
                "Failed to enrich lead {LeadId} ({CompanyName})",
                lead.Id, lead.Name
            );
        }

        return result;
    }

    private async Task<List<SearchResultInfo>> SearchCompanyInfoAsync(
        string companyName,
        string? industry,
        CancellationToken cancellationToken)
    {
        // Build search query: "Company Name industry website"
        var query = $"{companyName}";
        if (!string.IsNullOrWhiteSpace(industry))
            query += $" {industry}";
        query += " website official";

        return await PerformSearchAsync(query, "Company Info", cancellationToken);
    }

    private async Task<List<SearchResultInfo>> SearchContactInfoAsync(
        string contactName,
        string companyName,
        CancellationToken cancellationToken)
    {
        // Build search query: "Contact Name Company LinkedIn"
        var query = $"{contactName} {companyName} LinkedIn profile";

        return await PerformSearchAsync(query, "Contact Info", cancellationToken);
    }

    private async Task<List<SearchResultInfo>> SearchDomainInfoAsync(
        string website,
        CancellationToken cancellationToken)
    {
        // Extract domain from URL
        var domain = website.Replace("http://", "").Replace("https://", "").Split('/')[0];
        var query = $"site:{domain} OR {domain} company about";

        return await PerformSearchAsync(query, "Domain Info", cancellationToken);
    }

    private async Task<List<SearchResultInfo>> PerformSearchAsync(
        string query,
        string searchType,
        CancellationToken cancellationToken)
    {
        try
        {
            // Use DuckDuckGo provider (most reliable, no API keys needed)
            var searchService = _searchFactory.Create(ProviderType.DuckDuckGo);
            var options = new SearchOptions { MaxResults = 5 };

            var searchResults = await searchService.SearchAsync(query, options, cancellationToken);

            return searchResults.Select(r => new SearchResultInfo
            {
                Title = r.Title,
                Url = r.Url,
                Snippet = r.Snippet,
                SearchType = searchType,
                SearchQuery = query,
                Source = "DuckDuckGo"
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Search failed for query '{Query}' (type: {SearchType})",
                query, searchType
            );
            return new List<SearchResultInfo>();
        }
    }

    /// <summary>
    /// Gets cache statistics for monitoring performance.
    /// </summary>
    public CacheStatistics GetCacheStatistics()
    {
        // Access the cache from the factory's internal state
        // For now, return empty stats - can be enhanced if needed
        return new CacheStatistics
        {
            Hits = 0,
            Misses = 0
        };
    }
}

/// <summary>
/// Result of web search enrichment for a lead.
/// </summary>
public class LeadEnrichmentResult
{
    public Guid LeadId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int TotalResults { get; set; }
    public List<SearchResultInfo> SearchResults { get; set; } = new();
}

/// <summary>
/// Individual search result with metadata.
/// </summary>
public class SearchResultInfo
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string SearchType { get; set; } = string.Empty; // "Company Info", "Contact Info", "Domain Info"
    public string SearchQuery { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // "DuckDuckGo", "Google", "Bing"
}
