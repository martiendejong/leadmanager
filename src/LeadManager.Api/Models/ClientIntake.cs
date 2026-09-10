namespace LeadManager.Api.Models;

public enum IntakeProductType
{
    Website,
    AIEmployee,
    AITeam,
    Custom
}

public enum IntakeBundleType
{
    Starter,   // 50h / €125 per month
    Team,      // 200h / €500 per month
    PayPerHour // €3 per hour, no bundle
}

public enum IntakeStatus
{
    Draft,
    Estimated,
    AwaitingApproval,
    Approved,
    InProgress,
    Complete,
    Cancelled
}

public class ClientIntake
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client Client { get; set; } = null!;

    public IntakeProductType ProductType { get; set; } = IntakeProductType.Website;
    public IntakeBundleType BundleType { get; set; } = IntakeBundleType.Starter;
    public IntakeStatus Status { get; set; } = IntakeStatus.Draft;

    public string Requirements { get; set; } = "";
    public string? FirstTask { get; set; }
    public string? AdditionalNotes { get; set; }

    // Estimation (filled by AI after submission)
    public decimal? EstimatedHours { get; set; }
    public decimal? EstimatedPrice { get; set; }
    public string? EstimationReasoning { get; set; }

    // Approval tracking
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedByUserId { get; set; }

    // External system reference (minox-poc work queue item id)
    public string? WorkQueueItemId { get; set; }

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
