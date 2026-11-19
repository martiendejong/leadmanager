namespace LeadManager.Api.Models;

public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Plan { get; set; }
    public string? PrimaryContactName { get; set; }
    public string? PrimaryContactEmail { get; set; }
    public string? PrimaryContactPhone { get; set; }
    public string? Website { get; set; }
    public string? City { get; set; }
    public string? Sector { get; set; }
    public string? Notes { get; set; }
    // Engagement category: Active / Maintenance (maintenance contract) / Reactive (paid requests only)
    public string? EngagementCategory { get; set; }
    // Priority sub-scores, same idea as JengoWork: score = value * urgency / effort^2 (Fibonacci scale).
    public int? PriorityValue { get; set; }
    public int? PriorityUrgency { get; set; }
    public int? PriorityEffort { get; set; }
    public Guid? SourceLeadId { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    // Navigation
    public List<Project> Projects { get; set; } = new();
}
