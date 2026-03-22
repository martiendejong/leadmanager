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
    string? SalesApproach,
    // Owner identity (869ch51gb)
    string? OwnerLinkedInUrl,
    string? OwnerMobile,
    string? InternalContactName,
    string? InternalContactRole,
    // Operational fields (869ch50g9)
    string? WorkingArea,
    string? Certifications,
    string? PricingInfo,
    string? OpeningHours,
    // Sales priority label + reasoning (869ch50x8)
    string? SalesPriorityLabel,
    string? SalesPriorityReasoning,
    // Company signals (869ch4zb0)
    string? Signals,
    // Lead assignment (869ck3j4u)
    string? AssignedToUserId,
    // Pipeline status (869ck3j46)
    string PipelineStatus);

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
    bool SortDesc = false,
    string? AssignedToUserId = null);

public record CsvImportRowError(int Row, string Message);
public record CsvImportResultDto(int Created, int Skipped, List<CsvImportRowError> Errors);

public record LeadStatsDto(int Total, int Enriched, int NotEnriched);

public record ImportResultDto(int Imported, int Skipped, int Errors, List<string> ErrorDetails);

public record SetReminderDto(DateTime? ReminderDate);

public record LeadSearchRequest(string Sector, string? Location, int Limit = 25);
public record LeadSearchResult(string Name, string Website, string City, string Sector, string Phone, string Email, string Source, string Snippet = "", string? OwnerName = null, string? Description = null, string? Services = null, string? TargetAudience = null);
public record LeadSearchImportRequest(List<LeadSearchResult> Leads);

// Duplicate detection (869ck3j4y)
public record DuplicateLeadDto(Guid Id, string Name, string Website, string City, string Sector, int? Score);
public record DuplicateCheckResultDto(List<DuplicateLeadDto> Duplicates);

// Merge (869ck3j4y)
public record MergeLeadDto(Guid SourceLeadId);

// Assignment (869ck3j4u)
public record AssignLeadDto(string? UserId);
public record AssigneeDto(string UserId, string DisplayName, int LeadCount);

// Activity timeline (869ck3j4b)
public record CreateActivityDto(string ActivityType, string? Note);
public record LeadActivityDto(Guid Id, Guid LeadId, string? UserId, string ActivityType, string? Note, DateTime CreatedAt);

// Pipeline (869ck3j46)
public record UpdatePipelineStatusDto(string PipelineStatus);
