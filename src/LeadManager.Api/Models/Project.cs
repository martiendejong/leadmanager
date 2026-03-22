namespace LeadManager.Api.Models;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client Client { get; set; } = null!;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "Active";   // Active, Completed, On Hold
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
