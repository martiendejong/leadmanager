namespace LeadManager.Api.Models;

public class EnrichmentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending"; // Pending, Running, Completed, Failed
    public int TotalLeads { get; set; }
    public int ProcessedLeads { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<Guid> LeadIds { get; set; } = new(); // stored as JSON column
    public string? RequestedByUserId { get; set; }
}
