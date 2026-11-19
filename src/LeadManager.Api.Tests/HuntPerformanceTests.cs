using System.Diagnostics;
using System.Text;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Hunt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static LeadManager.Api.Tests.HuntJson;

namespace LeadManager.Api.Tests;

/// <summary>The tests that measure time or hold a very large page run alone, after the others, so a busy machine does not make them flaky.</summary>
[CollectionDefinition("Serial hunt performance", DisableParallelization = true)]
public class SerialHuntPerformance { }

[Collection("Serial hunt performance")]
public class HuntPerformanceTests
{
    private static string Nested(string tag, int depth, string inner = "Ik ben Tine van Wel, eigenaar. Bel 06 144 70 482")
    {
        var sb = new StringBuilder("<html><body>", depth * (tag.Length + 2) + 200);
        for (var i = 0; i < depth; i++) sb.Append('<').Append(tag).Append('>');
        sb.Append(inner);
        return sb.ToString(); // never closed: the worst case for a recursive walk
    }

    private static EvidenceFacts Extract(string html, string url = Site) =>
        new LeadEvidenceService().Extract(new[] { FetchedPage.Parse(url, 200, html) }, Site);

    [Theory]
    [InlineData("div")]
    [InlineData("span")]
    [InlineData("b")]
    [InlineData("a")]
    [InlineData("font")]
    [InlineData("section")]
    [InlineData("table")]
    [InlineData("ul")]
    public void A_page_that_nests_250000_tags_gives_no_text_no_facts_and_does_not_crash_or_hang(string tag)
    {
        // as deep as the size cap allows (250000 for the short tags)
        var depth = Math.Min(250_000, (SafePageFetcher.MaxBytes - 300) / (tag.Length + 2));
        var html = Nested(tag, depth);
        Assert.True(html.Length <= SafePageFetcher.MaxBytes, "the page fits the size cap, so the fetcher would deliver it");
        Assert.True(depth >= 150_000);
        var sw = Stopwatch.StartNew();

        Assert.True(SafeHtml.IsTooDeep(html));
        Assert.Null(SafeHtml.TryLoad(html));
        Assert.Equal("", HtmlText.ToText(html));
        var page = new FetchedPage(Site, 200, html, HtmlText.ToText(html));
        Assert.Null(page.Dom);
        Assert.Equal("", page.OwnerText);
        var facts = new LeadEvidenceService().Extract(new[] { page }, Site);
        Assert.Empty(facts.Mobiles);
        Assert.Empty(facts.Owners);
        Assert.Empty(facts.Small);

        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
    }


    [Theory]
    [InlineData("p")]
    [InlineData("li")]
    [InlineData("td")]
    [InlineData("option")]
    public void A_page_of_250000_nested_elements_with_optional_end_tags_cannot_overflow_either(string tag)
    {
        // These elements may leave out their end tag, so the pre-scan does not count them as nesting; the parser must then close or limit them.
        var html = Nested(tag, 100_000);
        var sw = Stopwatch.StartNew();
        var text = HtmlText.ToText(html);
        var page = new FetchedPage(Site, 200, html, text);
        var facts = new LeadEvidenceService().Extract(new[] { page }, Site);
        _ = page.OwnerText;
        Assert.True(sw.ElapsedMilliseconds < 6000, $"took {sw.ElapsedMilliseconds} ms (a parse is about 0.3 s)");
        Assert.NotNull(facts);
    }


    [Fact]
    public async Task The_whole_verification_of_a_deeply_nested_site_ends_without_facts_instead_of_crashing()
    {
        var f = new FakePageFetcher();
        f.Add(Site, Nested("div", 250_000) + "<a href=\"/contact\">Contact</a>");
        f.Add(Site + "contact", Nested("span", 250_000));
        var verifier = new LeadVerifier(f, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance);
        var sw = Stopwatch.StartNew();
        var site = await verifier.QuickLookAsync(Site, CancellationToken.None);
        await verifier.DeepenAsync(site, CancellationToken.None);
        var result = verifier.VerifyClaims(site, Element(GoodExtraction()));
        var det = verifier.VerifyDeterministic(site, "Tine van Wel");
        Assert.Empty(result.Verified);
        Assert.Empty(det.Verified);
        Assert.True(sw.ElapsedMilliseconds < 4000, $"took {sw.ElapsedMilliseconds} ms");
    }


    [Fact]
    public void A_huge_flat_page_with_many_numbers_is_processed_in_linear_time()
    {
        var sb = new StringBuilder("<html><body><ul>");
        for (var i = 0; i < 60_000; i++) sb.Append("<li>item ").Append(i).Append("<br>");
        for (var i = 0; i < 5000; i++) sb.Append("<p>Bel 06 ").Append((10 + i % 80).ToString("00")).Append(' ').Append((i % 90 + 10).ToString("00")).Append(' ').Append(((i * 7) % 90 + 10).ToString("00")).Append(' ').Append(((i * 13) % 90 + 10).ToString("00")).Append("</p>");
        sb.Append("</ul></body></html>");
        var html = sb.ToString();
        Assert.True(html.Length < SafePageFetcher.MaxBytes);

        // warm up (first use compiles the regular expressions), then time the real thing
        _ = new LeadEvidenceService().Extract(new[] { FetchedPage.Parse(Site, 200, "<p>Bel 06 12 34 56 78. Ik ben Tine van Wel, eigenaar.</p>") }, Site);

        var sw = Stopwatch.StartNew();
        var page = FetchedPage.Parse(Site, 200, html);   // what the fetcher does: one parse, the text from it
        var facts = new LeadEvidenceService().Extract(new[] { page }, Site);
        _ = page.OwnerText;
        Assert.True(sw.ElapsedMilliseconds < 4000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.NotEmpty(facts.Mobiles);
        Assert.Contains("item 59999", page.Text);
    }


    [Fact]
    public void A_page_of_many_sentences_and_names_is_processed_in_linear_time()
    {
        var sb = new StringBuilder("<html><body>");
        for (var i = 0; i < 30_000; i++) sb.Append("<p>Ik ben Jan Jansen, eigenaar. Zie ons team ").Append(i).Append(".</p>");
        var html = sb.ToString();
        var sw = Stopwatch.StartNew();
        var facts = Extract(html);
        Assert.True(sw.ElapsedMilliseconds < 4000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Single(facts.Owners);
    }
}
