namespace LeadManager.Api.Models;

public class LeadPageContent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public Lead Lead { get; set; } = null!;
    public string Url { get; set; } = "";
    public string RawText { get; set; } = "";
    public int HttpStatus { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

    public ICollection<LeadDocumentChunk> Chunks { get; set; } = new List<LeadDocumentChunk>();
}
