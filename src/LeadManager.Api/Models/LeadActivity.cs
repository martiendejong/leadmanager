namespace LeadManager.Api.Models;

public enum ActivityType
{
    Created,
    Enriched,
    StatusChanged,
    NoteAdded,
    EmailSent,
    Called,
    Converted
}

public class LeadActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public Lead Lead { get; set; } = null!;
    public string? UserId { get; set; }
    public ActivityType ActivityType { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
