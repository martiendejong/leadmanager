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

    // KvK enrichment fields
    public string? KvkNumber { get; set; }
    public string? VatNumber { get; set; }
    public string? Street { get; set; }
    public string? ZipCode { get; set; }
    public string? EmployeeCount { get; set; }
    public int? BranchCount { get; set; }
    public int? FoundingYear { get; set; }
    public string? LegalForm { get; set; }

    // Google enrichment fields
    public float? GoogleRating { get; set; }
    public int? GoogleReviewCount { get; set; }
    public string? GoogleMapsUrl { get; set; }

    // Social media fields
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public string? TwitterUrl { get; set; }

    // Business intelligence fields
    public bool IsPartOfGroup { get; set; } = false;
    public string? GroupName { get; set; }
    public string? NotableClients { get; set; }
    public int? SalesPriorityScore { get; set; }

    // Multi-input support fields (Task #3, #2, #1)
    public string? ManualInput { get; set; } // Max 5000 chars, free text input
    public bool HasUploadedDocuments { get; set; } = false; // True if documents uploaded
    public string? EnrichmentSources { get; set; } // JSON: {"ownerName":"manual","email":"website"}

    // AI Sales Approach (Task #7)
    public string? SalesApproach { get; set; } // JSON: see SalesApproachResult

    // Owner identity enrichment (869ch51gb)
    public string? OwnerLinkedInUrl { get; set; }   // Personal LinkedIn (/in/), not company page
    public string? OwnerMobile { get; set; }          // Owner's direct mobile number
    public string? InternalContactName { get; set; } // Fallback: receptionist / office manager
    public string? InternalContactRole { get; set; } // e.g. "Receptionist", "Office Manager"

    // Operational fields from website (869ch50g9)
    public string? WorkingArea { get; set; }     // Service region / regio
    public string? Certifications { get; set; }  // VCA, ISO, NEN, etc.
    public string? PricingInfo { get; set; }     // Hourly rate, pricing summary
    public string? OpeningHours { get; set; }    // Business hours

    // Sales priority label + reasoning (869ch50x8)
    public string? SalesPriorityLabel { get; set; }     // "High", "Medium", "Low"
    public string? SalesPriorityReasoning { get; set; } // e.g. "LinkedIn known, Mobile available"

    // Company signals layer (869ch4zb0)
    public string? Signals { get; set; } // JSON: List<LeadSignal>

    // Lead assignment (869ck3j4u)
    public string? AssignedToUserId { get; set; }
}
