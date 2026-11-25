namespace LeadManager.Api.Models;

public enum InvoiceStatus { Draft, Sent, Paid, Cancelled }

/// <summary>
/// An invoice that was (or will be) sent to a client for a service. The books stay in Fedha: this record ties the
/// agreement to what was actually invoiced so the next invoice can be derived. Sent and Paid invoices cover their period.
/// </summary>
public class ClientInvoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid? ServiceId { get; set; }
    public Guid? DocumentId { get; set; }
    /// <summary>Invoice number as in the books, for example 2026-15.</summary>
    public string Number { get; set; } = "";
    public DateTime InvoiceDate { get; set; } = DateTime.UtcNow;
    /// <summary>First and last day (inclusive) that the invoice covers.</summary>
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public decimal AmountExclVat { get; set; }
    public decimal VatRate { get; set; } = 21m;
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public string? SentTo { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? Notes { get; set; }
    /// <summary>Stable id from the reporting system (for example fedha:2026-15). Makes imports idempotent.</summary>
    public string? SourceRef { get; set; }
    /// <summary>When the invoice was sent according to the books (Fedha lastSentAt). Null together with SendUnknown means: not proven.</summary>
    public DateTime? SentAt { get; set; }
    /// <summary>True when the books say Sent, Overdue or Paid but hold no send date, so the sending cannot be proven.</summary>
    public bool SendUnknown { get; set; }
    /// <summary>Amount still open, including VAT, as reported by the books. Null for concepts and manual invoices.</summary>
    public decimal? OutstandingAmount { get; set; }
    /// <summary>Last time the Fedha sync touched this invoice.</summary>
    public DateTime? SyncedAt { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
