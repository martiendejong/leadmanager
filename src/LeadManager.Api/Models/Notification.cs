namespace LeadManager.Api.Models;

public enum NotificationType
{
    StaleLeadWarning,
    ReminderDue,
    EnrichmentComplete
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public NotificationType Type { get; set; }
    public string Message { get; set; } = "";
    public Guid? LinkedLeadId { get; set; }
    public bool IsRead { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
