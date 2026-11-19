namespace LeadManager.Api.Models;

public enum DocumentKind { Quote, Order, Agreement, Dpa, Nda, Invoice, Other }

public enum DocumentStatus { Draft, Sent, Signed, Rejected, Expired, Cancelled }

/// <summary>
/// A contractual or commercial document of a client: a quote, the order that came out of it, the signed
/// agreements (processing agreement, NDA, ...). Documents form a chain through PredecessorId, so a quote can
/// be followed by an order and that order by an agreement. The files themselves live on disk.
/// </summary>
public class ClientDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid? ProjectId { get; set; }
    /// <summary>The document this one follows from (quote -> order -> agreement).</summary>
    public Guid? PredecessorId { get; set; }
    public DocumentKind Kind { get; set; } = DocumentKind.Other;
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public string Title { get; set; } = "";
    /// <summary>Own number of the document, e.g. the quote number.</summary>
    public string? Reference { get; set; }
    public DateTime DocumentDate { get; set; } = DateTime.UtcNow;
    public DateTime? SignedAt { get; set; }
    public DateTime? ValidUntil { get; set; }
    /// <summary>Amount in euro, excluding VAT.</summary>
    public decimal? Amount { get; set; }
    public string? Notes { get; set; }
    /// <summary>Where the document lives when it is not (only) uploaded here.</summary>
    public string? Url { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ClientDocumentFile> Files { get; set; } = new();
}

public class ClientDocumentFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public ClientDocument Document { get; set; } = null!;
    /// <summary>Name shown to the user. Never used as a path.</summary>
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Generated, relative to the documents root. The user's file name never takes part in it.</summary>
    public string StoragePath { get; set; } = "";
    public string? Label { get; set; }
    public string? UploadedByUserId { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
