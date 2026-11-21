namespace LeadManager.Api.Models;

public enum ServiceKind { WebsiteManagement, Hosting, Maintenance, Development, Consulting, Other }

public enum ServiceStatus { Proposed, Active, Paused, Ended }

/// <summary>
/// A service we deliver to a client (website management, hosting, ...). Contracts and concepts link to it
/// through <see cref="ClientDocument.ServiceId"/>. Invoicing stays in Fedha; MonthlyAmount is informative.
/// </summary>
public class ClientService
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public ServiceKind Kind { get; set; } = ServiceKind.Other;
    public ServiceStatus Status { get; set; } = ServiceStatus.Proposed;
    public string Name { get; set; } = "";
    public Guid? ProjectId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    /// <summary>Monthly amount in euro, excluding VAT (informative; Fedha invoices).</summary>
    public decimal? MonthlyAmount { get; set; }
    public string? Notes { get; set; }
    /// <summary>Stable id from the system that reported it (Fedha, Jengo Knowledge claim). Makes imports idempotent.</summary>
    public string? SourceRef { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
