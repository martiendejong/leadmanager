namespace LeadManager.Api.DTOs;

public record LeadDto(
    Guid Id,
    string Name,
    string Website,
    string Sector,
    string City,
    string Phone,
    string CompanyEmail,
    string OwnerName,
    string OwnerFirstName,
    string OwnerLastName,
    string PersonalEmail,
    string AnymailfinderResult,
    string LinkedInUrl,
    string Source,
    bool IsEnriched,
    DateTime? EnrichedAt,
    DateTime ImportedAt,
    DateTime CreatedAt,
    string? ImportedByUserId,
    // RAG enrichment fields
    string? OwnerTitle,
    string? Description,
    string? Services,
    string? TargetAudience,
    string? WebsiteStatus,
    string? ResolvedUrl,
    DateTime? CrawledAt,
    int EnrichmentVersion,
    int PagesCrawled,
    int ChunksIndexed,
    string? AiSummary);

public record CreateLeadDto(
    string Name,
    string Website,
    string Sector,
    string City,
    string Phone,
    string CompanyEmail,
    string Source);

public record UpdateLeadDto(
    string Name,
    string Website,
    string Sector,
    string City,
    string Phone,
    string CompanyEmail,
    string Source,
    string OwnerName,
    string OwnerFirstName,
    string OwnerLastName,
    string PersonalEmail,
    string LinkedInUrl);

public record LeadFilterParams(
    bool? Enriched = null,
    DateTime? EnrichedAfter = null,
    DateTime? EnrichedBefore = null,
    int Page = 1,
    int PageSize = 50,
    string SortBy = "name",
    bool SortDesc = false);

public record LeadStatsDto(int Total, int Enriched, int NotEnriched);

public record ImportResultDto(int Imported, int Skipped, int Errors, List<string> ErrorDetails);

public record LeadSearchRequest(string Sector, string? Location, int Limit = 25);
public record LeadSearchResult(string Name, string Website, string City, string Sector, string Phone, string Email, string Source);
public record LeadSearchImportRequest(List<LeadSearchResult> Leads);
