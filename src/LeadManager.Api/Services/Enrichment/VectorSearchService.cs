using System.Text.Json;
using LeadManager.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Enrichment;

public class VectorSearchService
{
    public async Task<List<string>> SearchAsync(
        LeadManagerDbContext db,
        Guid leadId,
        float[] queryEmbedding,
        int topK = 3)
    {
        var chunks = await db.LeadDocumentChunks
            .Where(c => c.LeadId == leadId)
            .Select(c => new { c.ChunkText, c.EmbeddingJson })
            .ToListAsync();

        if (chunks.Count == 0) return new();

        var scored = chunks
            .Select(c =>
            {
                float[]? embedding = null;
                try { embedding = JsonSerializer.Deserialize<float[]>(c.EmbeddingJson); } catch { }
                var score = embedding != null ? CosineSimilarity(queryEmbedding, embedding) : 0f;
                return (c.ChunkText, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.ChunkText)
            .ToList();

        return scored;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom == 0 ? 0f : dot / denom;
    }
}
