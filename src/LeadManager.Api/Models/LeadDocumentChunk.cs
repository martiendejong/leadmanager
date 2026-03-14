namespace LeadManager.Api.Models;

public class LeadDocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public Lead Lead { get; set; } = null!;
    public Guid PageContentId { get; set; }
    public LeadPageContent PageContent { get; set; } = null!;
    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = "";
    public string EmbeddingJson { get; set; } = "[]"; // float[] stored as JSON
}
