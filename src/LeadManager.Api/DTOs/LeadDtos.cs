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
    string Status,
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
    string? AiSummary,
    string? SalesPitch,
    // KvK enrichment fields
    string? KvkNumber,
    string? VatNumber,
    string? Street,
    string? ZipCode,
    string? EmployeeCount,
    int? BranchCount,
    int? FoundingYear,
    string? LegalForm,
    // Google enrichment fields
    float? GoogleRating,
    int? GoogleReviewCount,
    string? GoogleMapsUrl,
    // Social media fields
    string? FacebookUrl,
    string? InstagramUrl,
    string? TwitterUrl,
    // Business intelligence fields
    bool IsPartOfGroup,
    string? GroupName,
    string? NotableClients,
    int? SalesPriorityScore,
    // Multi-input support fields
    string? ManualInput,
    bool HasUploadedDocuments,
    string? EnrichmentSources,
    // AI Sales Approach (Task #7)
    string? SalesApproach);

public record CreateLeadDto(
    string Name,
    string? Website,  // Made optional - Task #4
    string Sector,
    string City,
    string Phone,
    string CompanyEmail,
    string Source,
    string? ManualInput = null);  // Task #3

public record UpdateLeadDto(
    string Name,
    string? Website,  // Made optional - Task #4
    string Sector,
    string City,
    string Phone,
    string CompanyEmail,
    string Source,
    string OwnerName,
    string OwnerFirstName,
    string OwnerLastName,
    string PersonalEmail,
    string LinkedInUrl,
    string? ManualInput = null);  // Task #3

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
public record LeadSearchResult(string Name, string Website, string City, string Sector, string Phone, string Email, string Source, string Snippet = "", string? OwnerName = null, string? Description = null, string? Services = null, string? TargetAudience = null);
public record LeadSearchImportRequest(List<LeadSearchResult> Leads);

// Lead Notes DTOs
public record LeadNoteDto(Guid Id, Guid LeadId, string Content, DateTime CreatedAt, string CreatedByUserId, string? CreatedByName);
public record CreateLeadNoteDto(string Content);
public record UpdateLeadNoteDto(string Content);

// Lead Status DTO
public record UpdateLeadStatusDto(string Status);
