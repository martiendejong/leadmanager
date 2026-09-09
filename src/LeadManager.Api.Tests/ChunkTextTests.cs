using LeadManager.Api.Services.Enrichment;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LeadManager.Api.Tests;

// Regression tests for the ChunkText infinite loop (42.5 GB incident 27-08,
// restart-storm 09-09): the old loop stepped backward on the final chunk and
// re-added the same tail slice until the memory watchdog killed the process.
public class ChunkTextTests
{
    private static EmbeddingService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "test-key" })
            .Build();
        return new EmbeddingService(new HttpClient(), config);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(81)]      // > overlap: the old loop never terminated here
    [InlineData(500)]     // exactly one chunk size
    [InlineData(501)]
    [InlineData(10_000)]  // max page text length used by PageFetcher
    public void ChunkText_terminates_and_stays_bounded(int length)
    {
        var service = CreateService();
        var text = new string('a', length);

        var chunks = service.ChunkText(text);

        Assert.NotEmpty(chunks);
        Assert.True(chunks.Count <= 200, $"chunk count {chunks.Count} exceeds hard cap");
    }

    [Fact]
    public void ChunkText_covers_full_text()
    {
        var service = CreateService();
        var text = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"woord{i}"));

        var chunks = service.ChunkText(text);

        Assert.Contains("woord0", chunks.First());
        Assert.Contains("woord399", chunks.Last());
    }

    [Fact]
    public void ChunkText_handles_sentence_boundaries_with_many_periods()
    {
        var service = CreateService();
        // Dotted-leader style content (menus, price lists) that biases the
        // sentence-boundary search toward early periods.
        var text = string.Concat(Enumerable.Repeat("item....... 9,99 ", 600));

        var chunks = service.ChunkText(text);

        Assert.NotEmpty(chunks);
        Assert.True(chunks.Count <= 200);
    }

    [Fact]
    public void ChunkText_empty_and_whitespace_return_no_chunks()
    {
        var service = CreateService();
        Assert.Empty(service.ChunkText(""));
        Assert.Empty(service.ChunkText("   \n\t  "));
    }
}
