namespace LeadManager.Api.Models;

public enum ServiceKind { WebsiteManagement, Hosting, Maintenance, Development, Consulting, Other }

public enum ServiceStatus { Proposed, Active, Paused, Ended }

public enum BillingInterval { Monthly, Quarterly, Yearly, OneOff, Hourly }

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
    /// <summary>How often this service is invoiced.</summary>
    public BillingInterval BillingInterval { get; set; } = BillingInterval.Monthly;
    /// <summary>Amount per billing period in euro, excluding VAT. Empty: MonthlyAmount is used (monthly interval).</summary>
    public decimal? BillingAmount { get; set; }
    /// <summary>Hourly rate in euro excluding VAT (interval Hourly).</summary>
    public decimal? HourlyRate { get; set; }
    /// <summary>VAT percentage; empty means the default (21).</summary>
    public decimal? VatRate { get; set; }
    /// <summary>Paid or settled up to and including this date (for example a year paid ahead).</summary>
    public DateTime? PaidUntil { get; set; }
    /// <summary>Free of charge up to and including this date (for example a free year as part of a deal).</summary>
    public DateTime? FreeUntil { get; set; }
    public string? FreeReason { get; set; }
    /// <summary>Remark for the invoice, e.g. the description line or the agreed payment term.</summary>
    public string? InvoiceNote { get; set; }
    public string? Notes { get; set; }
    /// <summary>Stable id from the system that reported it (Fedha, Jengo Knowledge claim). Makes imports idempotent.</summary>
    public string? SourceRef { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
