using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LeadManager.Api.Services.Hunt;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>The focused review of the regular expressions and the XML parser of the hunt.</summary>
public class HuntReviewFourTests
{
    private static string RepoSource(params string[] parts) =>
        Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "LeadManager.Api" }.Concat(parts).ToArray()));

    // ---- every regular expression has a match timeout ---------------------------------------------------------------------

    [Fact]
    public void No_regular_expression_of_the_hunt_is_built_without_a_match_timeout()
    {
        foreach (var file in Directory.GetFiles(RepoSource("Services", "Hunt"), "*.cs"))
        {
            if (file.EndsWith("HuntRegex.cs")) continue;
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("new Regex(", source);
            Assert.False(Regex.IsMatch(source, @"(?<![\w])(?:System\.Text\.RegularExpressions\.)?Regex\.(IsMatch|Replace|Match|Matches|Split)\("), file + " uses a static Regex method without a timeout");
            Assert.False(Regex.IsMatch(source, @"Regex\s+\w+\s*=\s*new\("), file + " builds a field without a timeout");
        }
    }

    [Fact]
    public void The_hunt_regex_helpers_carry_the_timeout_and_a_catastrophic_pattern_ends_after_it()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), HuntRegex.New("a").MatchTimeout);
        var sw = Stopwatch.StartNew();
        Assert.Throws<RegexMatchTimeoutException>(() => HuntRegex.IsMatch(new string('a', 60) + "!", "^(a+)+$"));
        Assert.True(sw.ElapsedMilliseconds < 5000);
        Assert.Throws<RegexMatchTimeoutException>(() => HuntRegex.Replace(new string('a', 60) + "!", "^(a+)+$", "x"));
    }

    [Fact]
    public void A_name_pattern_that_times_out_is_no_match_and_the_page_is_still_read()
    {
        // the rules of a page run one by one; one that times out finds nothing and does not stop the others (see LeadEvidenceService.Extract)
        var html = "<p>Bel 06 144 70 482</p><p>" + new string('A', 200_000) + "</p>";
        var facts = new LeadEvidenceService().Extract(new[] { FetchedPage.Parse(HuntJson.Site, 200, html) }, HuntJson.Site);
        Assert.Single(facts.Mobiles);
    }

    // ---- the XML parser ------------------------------------------------------------------------------------------------------

    private const string Sitemap = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        "<url><loc>https://www.tinevanwel.nl/</loc></url><url><loc>https://www.tinevanwel.nl/over-mij</loc></url></urlset>";

    [Fact]
    public void A_plain_sitemap_is_read()
    {
        var doc = SafeXml.TryParse(Sitemap);
        Assert.NotNull(doc);
        Assert.Equal(2, doc!.Descendants().Count(e => e.Name.LocalName == "loc"));
    }

    [Fact]
    public void A_billion_laughs_document_is_refused_quickly_without_expanding()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\">");
        var prev = "lol";
        for (var i = 1; i <= 9; i++)
        {
            var name = "lol" + i;
            sb.Append("<!ENTITY ").Append(name).Append(" \"").Append(string.Concat(Enumerable.Repeat("&" + prev + ";", 10))).Append("\">");
            prev = name;
        }
        sb.Append("]><urlset><url><loc>&").Append(prev).Append(";</loc></url></urlset>");
        var sw = Stopwatch.StartNew();
        Assert.Null(SafeXml.TryParse(sb.ToString()));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        var memory = GC.GetTotalMemory(false);
        Assert.True(memory < 500_000_000);
    }

    [Fact]
    public void A_document_with_a_doctype_or_an_external_entity_is_refused_and_nothing_is_fetched()
    {
        Assert.Null(SafeXml.TryParse("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><urlset><url><loc>&e;</loc></url></urlset>"));
        Assert.Null(SafeXml.TryParse("<?xml version=\"1.0\"?><!DOCTYPE x SYSTEM \"http://169.254.169.254/evil.dtd\"><urlset/>"));
    }

    [Fact]
    public void A_deeply_nested_or_huge_or_broken_document_is_refused()
    {
        var deep = new StringBuilder();
        for (var i = 0; i < 200_000; i++) deep.Append("<a>");
        var sw = Stopwatch.StartNew();
        Assert.Null(SafeXml.TryParse(deep.ToString()));
        Assert.Null(SafeXml.TryParse(string.Concat(Enumerable.Repeat("<a>", SafeXml.MaxDepth + 5)) + string.Concat(Enumerable.Repeat("</a>", SafeXml.MaxDepth + 5))));
        Assert.True(sw.ElapsedMilliseconds < 3000);

        Assert.Null(SafeXml.TryParse("<urlset>" + new string('x', SafeXml.MaxCharacters) + "</urlset>"));   // over the size cap
        Assert.Null(SafeXml.TryParse("<urlset><url>"));                                                          // not well formed
        Assert.Null(SafeXml.TryParse(""));
        Assert.Null(SafeXml.TryParse(null));
        // a normal depth passes
        Assert.NotNull(SafeXml.TryParse(string.Concat(Enumerable.Repeat("<a>", 20)) + string.Concat(Enumerable.Repeat("</a>", 20))));
    }

    [Fact]
    public void The_sitemap_service_reads_xml_only_through_safexml()
    {
        var source = File.ReadAllText(RepoSource("Services", "Enrichment", "SitemapService.cs"));
        Assert.DoesNotContain("XDocument.Parse", source);
        Assert.Contains("SafeXml.TryParse", source);
        foreach (var file in Directory.GetFiles(RepoSource("Services"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith("SafeXml.cs")) continue;
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("XDocument.Parse", text);
            Assert.DoesNotContain("new XmlDocument", text);
        }
    }
}
