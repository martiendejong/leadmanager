namespace LeadManager.Api.Models;

/// <summary>Manual link between a Fedha contact and a client, for contacts whose name differs from the client name.</summary>
public class FedhaContactLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public int FedhaContactId { get; set; }
    public string FedhaContactName { get; set; } = "";
    public Guid ClientId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One run of the Fedha invoice sync (kept for the status view). Fedha stays the owner of invoicing.</summary>
public class FedhaSyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string Trigger { get; set; } = "manual";
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Unmatched { get; set; }
    public string? Error { get; set; }
    /// <summary>JSON list of Fedha contacts that have invoices but no client.</summary>
    public string? UnmatchedJson { get; set; }
}
