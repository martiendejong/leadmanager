namespace LeadManager.Api.Models;

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
}
