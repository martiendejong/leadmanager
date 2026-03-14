using System.Text;
using System.Text.Json;

namespace LeadManager.Api.Services.Enrichment;

public class EmbeddingService
{
    private readonly HttpClient _http;
    private const string Model = "text-embedding-3-small";
    private const int ChunkSize = 500;   // chars (approx 125 tokens)
    private const int ChunkOverlap = 80; // chars overlap

    public EmbeddingService(IConfiguration configuration)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public List<string> ChunkText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();

        var chunks = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + ChunkSize, text.Length);

            // Try to break at a sentence boundary
            if (end < text.Length)
            {
                int lastPeriod = text.LastIndexOf('.', end, Math.Min(80, end - start));
                if (lastPeriod > start)
                    end = lastPeriod + 1;
            }

            var chunk = text[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
                chunks.Add(chunk);

            start = end - ChunkOverlap;
            if (start >= text.Length) break;
        }
        return chunks;
    }

    public async Task<float[]> EmbedAsync(string text)
    {
        var embeddings = await EmbedBatchAsync(new[] { text });
        return embeddings.FirstOrDefault() ?? Array.Empty<float>();
    }

    public async Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> texts)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return new();

        var results = new List<float[]>();

        // Process in batches of 20
        for (int i = 0; i < textList.Count; i += 20)
        {
            var batch = textList.Skip(i).Take(20).ToList();
            var requestBody = new
            {
                model = Model,
                input = batch
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("https://api.openai.com/v1/embeddings", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var data = doc.RootElement.GetProperty("data");

            // OpenAI returns embeddings in the same order as input
            var batchEmbeddings = new List<(int index, float[] vec)>();
            foreach (var item in data.EnumerateArray())
            {
                var idx = item.GetProperty("index").GetInt32();
                var embedding = item.GetProperty("embedding").EnumerateArray()
                    .Select(e => e.GetSingle())
                    .ToArray();
                batchEmbeddings.Add((idx, embedding));
            }

            results.AddRange(batchEmbeddings.OrderBy(x => x.index).Select(x => x.vec));
        }

        return results;
    }
}
