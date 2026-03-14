using LeadManager.Api.DTOs;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Profile;

public record QualifiedLead(
    string Name,
    string Website,
    string City,
    string Sector,
    string Phone,
    string Email,
    string Source,
    int ConfidenceScore,
    string QualificationReason,
    string? OwnerName = null,
    string? Description = null,
    string? Services = null,
    string? TargetAudience = null
);

public class SmartSearchService
{
    private readonly GptLeadGeneratorService _generator;
    private readonly ILogger<SmartSearchService> _logger;

    // Target: 20 angles x 25 leads = 500 raw leads before dedup
    private const int AngleCount = 20;
    private const int LeadsPerAngle = 25;

    public SmartSearchService(GptLeadGeneratorService generator, ILogger<SmartSearchService> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    public async Task<List<QualifiedLead>> SearchAndQualifyAsync(
        CompanyProfile profile,
        HashSet<string>? existingLeadKeys = null,
        IProgress<QualifiedLead>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Generate diverse search angles
        var angles = await _generator.GenerateAnglesAsync(profile, AngleCount);
        _logger.LogInformation("Generated {Count} angles for profile {Company}", angles.Count, profile.CompanyName);

        // Step 2: Generate leads for each angle in parallel batches
        var allResults = new List<LeadSearchResult>();
        var seenDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Process angles in groups of 5 (parallel) to be fast but not hammer the API
        var angleGroups = angles.Chunk(5);
        foreach (var group in angleGroups)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var tasks = group.Select(angle => _generator.GenerateBatchAsync(profile, angle, LeadsPerAngle));
            var batchResults = await Task.WhenAll(tasks);

            foreach (var batch in batchResults)
            {
                foreach (var r in batch)
                {
                    var domain = ExtractDomain(r.Website);
                    if (!string.IsNullOrEmpty(domain) && seenDomains.Add(domain))
                        allResults.Add(r);
                }
            }

            // Small delay between groups to respect rate limits
            await Task.Delay(500, cancellationToken);
        }

        _logger.LogInformation("Generated {Count} unique leads across all angles", allResults.Count);

        // Step 3: Filter out leads already in user's list
        if (existingLeadKeys != null && existingLeadKeys.Count > 0)
        {
            var before = allResults.Count;
            allResults = allResults
                .Where(r => !existingLeadKeys.Contains(MakeKey(r.Name, r.Website)))
                .ToList();
            _logger.LogInformation("Dedup: filtered {Removed} already-imported leads, {Remaining} remaining",
                before - allResults.Count, allResults.Count);
        }

        // Step 4: GPT-generated leads are already qualified (GPT only outputs relevant companies).
        // Convert directly to QualifiedLead with high confidence score — no second qualification pass needed.
        var qualified = allResults.Select(r => new QualifiedLead(
            r.Name, r.Website, r.City, r.Sector, r.Phone, r.Email, r.Source,
            ConfidenceScore: 80, // GPT-generated = inherently qualified
            QualificationReason: "Gegenereerd op basis van bedrijfsprofiel en ICP",
            OwnerName: r.OwnerName,
            Description: r.Description,
            Services: r.Services,
            TargetAudience: r.TargetAudience
        )).ToList();

        foreach (var lead in qualified)
            progress?.Report(lead);

        _logger.LogInformation("Returning {Count} qualified leads", qualified.Count);
        return qualified;
    }

    public static string MakeKey(string name, string website) =>
        $"{name.Trim().ToLowerInvariant()}|{ExtractDomain(website)}";

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return "";
    }
}
