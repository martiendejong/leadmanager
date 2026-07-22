namespace LeadManager.Api.Models;

public class LeadNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedByUserId { get; set; } = "";

    // Navigation properties
    public Lead? Lead { get; set; }
    public ApplicationUser? CreatedBy { get; set; }
}
