namespace LeadManager.Api.Models;

public enum WebsiteStatus { Unknown, Reachable, Unreachable }

public class Lead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Website { get; set; } = "";
    public string Sector { get; set; } = "";
    public string City { get; set; } = "";
    public string Phone { get; set; } = "";
    public string CompanyEmail { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string OwnerFirstName { get; set; } = "";
    public string OwnerLastName { get; set; } = "";
    public string PersonalEmail { get; set; } = "";
    public string AnymailfinderResult { get; set; } = "";
    public string LinkedInUrl { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsEnriched { get; set; } = false;
    public DateTime? EnrichedAt { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? ImportedByUserId { get; set; }

    // RAG enrichment fields
    public string? OwnerTitle { get; set; }
    public string? Description { get; set; }
    public string? Services { get; set; }
    public string? TargetAudience { get; set; }
    public WebsiteStatus WebsiteStatus { get; set; } = WebsiteStatus.Unknown;
    public string? ResolvedUrl { get; set; }
    public DateTime? CrawledAt { get; set; }
    public int EnrichmentVersion { get; set; } = 0;
    public int PagesCrawled { get; set; } = 0;
    public int ChunksIndexed { get; set; } = 0;
    public string? AiSummary { get; set; }
    public string? SalesPitch { get; set; }
}
