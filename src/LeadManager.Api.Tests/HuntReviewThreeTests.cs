using System.Diagnostics;
using System.Text;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Hunt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static LeadManager.Api.Tests.HuntJson;

namespace LeadManager.Api.Tests;

/// <summary>Negative tests of the third independent review of PR 66.</summary>
public class HuntReviewThreeTests
{
    private static string Nested(string tag, int depth, string inner = "Ik ben Tine van Wel, eigenaar. Bel 06 144 70 482")
    {
        var sb = new StringBuilder("<html><body>", depth * (tag.Length + 2) + 200);
        for (var i = 0; i < depth; i++) sb.Append('<').Append(tag).Append('>');
        sb.Append(inner);
        return sb.ToString(); // never closed: the worst case for a recursive walk
    }

    private static EvidenceFacts Extract(string html, string url = Site) =>
        new LeadEvidenceService().Extract(new[] { new FetchedPage(url, 200, html, HtmlText.ToText(html)) }, Site);

    // ---- 1: a hostile page that nests tags a quarter of a million deep must not take the process down --------------

    [Fact]
    public void The_depth_limit_accepts_a_real_deep_page_and_refuses_one_just_past_it()
    {
        Assert.False(SafeHtml.IsTooDeep(Nested("div", SafeHtml.MaxDepth - 5) + string.Concat(Enumerable.Repeat("</div>", SafeHtml.MaxDepth - 5))));
        Assert.True(SafeHtml.IsTooDeep(Nested("div", SafeHtml.MaxDepth + 5)));
        var ok = Nested("div", 90);
        Assert.Contains("Tine van Wel", HtmlText.ToText(ok));
    }

    [Fact]
    public void Tags_in_comments_scripts_and_attribute_values_do_not_count_as_nesting_and_end_tags_close_it()
    {
        var many = string.Concat(Enumerable.Repeat("<div>", 5000));
        Assert.False(SafeHtml.IsTooDeep("<html><body><!-- " + many + " --><p>Welkom</p></body></html>"));
        Assert.False(SafeHtml.IsTooDeep("<html><body><script>var s = '" + many + "';</script><p>Welkom</p></body></html>"));
        Assert.False(SafeHtml.IsTooDeep("<html><body><a title=\"" + many + "\" href=\"/x\">link</a><p>Welkom</p></body></html>"));
        Assert.False(SafeHtml.IsTooDeep("<p>a < b and <3 and <div></div>"));
        // closed again: a sequence of many sibling elements is flat
        Assert.False(SafeHtml.IsTooDeep(string.Concat(Enumerable.Repeat("<div><span>x</span></div>", 50_000))));
        // a long list whose items are never closed is flat, not deep
        Assert.False(SafeHtml.IsTooDeep("<ul>" + string.Concat(Enumerable.Repeat("<li>item", 50_000)) + "</ul>"));
    }

    [Fact]
    public void The_html_parser_version_is_pinned_and_every_document_comes_from_safehtml()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LeadManager.Api");
        var csproj = File.ReadAllText(Path.Combine(root, "LeadManager.Api.csproj"));
        Assert.Matches(@"HtmlAgilityPack""\s+Version=""\d+\.\d+\.\d+""", csproj);
        Assert.DoesNotContain("HtmlAgilityPack\" Version=\"1.*\"", csproj);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories))   // the older enrichment parses web pages too
        {
            if (file.EndsWith("SafeHtml.cs")) continue;
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("new HtmlDocument", source);
            Assert.DoesNotContain(".LoadHtml(", source);
        }
    }

    // ---- 2: CPU: a huge flat page is handled in linear time -----------------------------------------------------

    // ---- helpers for the rule tests ---------------------------------------------------------------------------------

    private static FakePageFetcher Pages(string home, string contact = "<p>Contact</p>", string about = "<p>Over mij</p>")
    {
        var f = new FakePageFetcher();
        f.Add(Site, home + "<a href=\"/contact\">Contact</a> <a href=\"/over-mij\">Over mij</a>");
        f.Add(Site + "contact", contact);
        f.Add(Site + "over-mij", about);
        return f;
    }

    private static async Task<VerificationResult> Deterministic(FakePageFetcher f, string? knownOwner = null)
    {
        var verifier = new LeadVerifier(f, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance);
        var site = await verifier.QuickLookAsync(Site, CancellationToken.None);
        await verifier.DeepenAsync(site, CancellationToken.None);
        return verifier.VerifyDeterministic(site, knownOwner);
    }

    private static async Task<(VerificationResult Result, Lead Lead)> Claims(FakePageFetcher f, string claimsJson)
    {
        var verifier = new LeadVerifier(f, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance);
        var site = await verifier.QuickLookAsync(Site, CancellationToken.None);
        await verifier.DeepenAsync(site, CancellationToken.None);
        var result = verifier.VerifyClaims(site, Element(claimsJson));
        var lead = new Lead { Name = "Tine", Website = Site };
        LeadVerifier.Apply(lead, result, clearUnverified: false, new SalesScoreService());
        return (result, lead);
    }

    // ---- 3: the phone a lead already has follows the same rule ------------------------------------------------------

    [Fact]
    public async Task A_mobile_in_the_phone_field_that_only_stands_next_to_a_credit_is_not_kept_when_the_server_wrote_it()
    {
        var f = Pages("<p>Welkom</p>", contact: "<p>Bel mij op 06 144 70 482</p>", about: "<p>Website door Studio Pixel, bel 06 99 88 77 66</p>");
        var result = await Deterministic(f);
        var scoring = new SalesScoreService();

        var writtenByServer = new Lead { Name = "Tine", Website = Site, Phone = "06 99 88 77 66" };
        HuntProvenance.Record(writtenByServer, new[] { "phone" });
        var removed = LeadVerifier.Apply(writtenByServer, result, clearUnverified: true, scoring, null, HuntService.MayClear(writtenByServer, false));
        Assert.Equal("", writtenByServer.Phone);
        Assert.Contains(removed, x => x.Contains("phone"));

        var typed = new Lead { Name = "Tine", Website = Site, Phone = "06 99 88 77 66" };
        var kept = new List<string>();
        LeadVerifier.Apply(typed, result, clearUnverified: true, scoring, kept, HuntService.MayClear(typed, false));
        Assert.Equal("06 99 88 77 66", typed.Phone);               // typed by a person: kept, and listed
        Assert.Contains(kept, x => x.Contains("phone"));

        var contact = new Lead { Name = "Tine", Website = Site, Phone = "06 144 70 482" };
        HuntProvenance.Record(contact, new[] { "phone" });
        LeadVerifier.Apply(contact, result, clearUnverified: true, scoring, null, HuntService.MayClear(contact, false));
        Assert.Equal("06 144 70 482", contact.Phone);             // the business's own number in its contact block: stays
    }

    // ---- 4: testimonials are not the owner ------------------------------------------------------------------------

    [Theory]
    [InlineData("<blockquote>Geweldig werk! Jan Jansen, eigenaar</blockquote>")]
    [InlineData("<div class=\"testimonial-slider\"><p>Piet Pietersen, directeur</p></div>")]
    [InlineData("<section id=\"reviews\"><p>Anna de Vries, eigenaar</p></section>")]
    [InlineData("<p>\"Super service!\"</p><p>Anna de Vries, eigenaar</p>")]
    [InlineData("<p>Marieke de Boer, directeur Bakkerij Boer</p>")]
    [InlineData("<p>Piet Pietersen, eigenaar van Studio Pixel</p>")]
    [InlineData("<!-- Mijn naam is Piet Bakker, eigenaar --><p>Welkom</p>")]
    public void A_person_in_a_quotation_a_testimonial_or_another_organisation_is_not_the_owner(string html) =>
        Assert.Empty(Extract(html).Owners);

    [Theory]
    [InlineData("<p>Tine van Wel, eigenaar</p>")]
    [InlineData("<p>Tine van Wel, eigenaar van Tinevanwel Ontwerp</p>")]
    [InlineData("<h1>Mijn naam is Tine van Wel</h1>")]
    public void The_owner_named_on_the_businesss_own_site_still_counts(string html) =>
        Assert.Contains(Extract(html).Owners, o => o.Name == "Tine van Wel");

    [Fact]
    public async Task A_name_next_to_a_role_only_confirms_a_name_the_lead_already_has()
    {
        var f = Pages("<p>Jan Jansen, eigenaar</p>");
        var none = await Deterministic(f);                   // the lead has no owner: a bare name and role is not enough
        Assert.DoesNotContain("ownerName", none.Verified.Keys);
        var confirmed = await Deterministic(f, "Jan Jansen");
        Assert.Equal("Jan Jansen", confirmed.Verified["ownerName"].Value);
    }

    [Fact]
    public void A_one_person_statement_inside_a_testimonial_is_not_the_business_speaking()
    {
        Assert.Empty(Extract("<blockquote>Ik ben zelfstandig fotograaf en tevreden over de service.</blockquote>").Small);
        Assert.NotEmpty(Extract("<p>Ik ben zelfstandig fotograaf.</p>").Small);
    }

    // ---- 5: a year of a statement the business makes about itself -------------------------------------------------

    [Theory]
    [InlineData("Sinds 2008 is er veel veranderd in de praktijk.", false)]
    [InlineData("Sinds 2004 werken wij samen met bureau X.", false)]
    [InlineData("Sinds 2019 ben ik lid van de NOAB.", false)]
    [InlineData("Vanaf 2010 werk ik als zelfstandig ontwerper.", false)]
    [InlineData("Onze nieuwsbrief is gestart in 2008.", false)]
    [InlineData("Sinds 2009 werk ik als zelfstandig tekstschrijver.", true)]
    [InlineData("Actief sinds 2009 als ontwerper.", true)]
    [InlineData("Ik ben zelfstandig tekstschrijver sinds 2009.", true)]
    [InlineData("Mijn praktijk bestaat sinds 2010.", true)]
    [InlineData("Het bedrijf is opgericht in 2008.", true)]
    [InlineData("In 2010 ben ik met Goldenbeld Advies gestart.", true)]
    public void A_year_counts_only_in_a_statement_of_the_business_about_itself(string sentence, bool expected) =>
        Assert.Equal(expected, Extract($"<p>{sentence}</p>").Years.Any(y => y.Kind != "Copyright"));

    [Fact]
    public async Task The_year_comes_from_the_best_ranked_statement_not_from_the_earliest_year_on_the_site()
    {
        var f = Pages("<p>Welkom</p>", about: "<p>Sinds 2005 werk ik als ontwerper.</p><p>Mijn bedrijf is opgericht in 2012.</p>");
        var result = await Deterministic(f);
        Assert.Equal("2012", result.Verified["foundingYear"].Value);       // 'opgericht in' beats 'sinds', and 2005 is not taken as the earliest year
    }

    // ---- 6: a company link must be the business's own --------------------------------------------------------------

    [Fact]
    public async Task A_company_linkedin_link_in_a_text_about_clients_is_not_the_businesss_own()
    {
        var f = Pages("<p>Wij werken voor <a href=\"https://www.linkedin.com/company/klant-bv\">onze klant</a>.</p>" +
                      "<footer><a href=\"https://www.linkedin.com/company/ontwerpstudio-x\">LinkedIn</a></footer>" +
                      "<p>Volg ons: <a href=\"https://www.linkedin.com/company/tinevanwel-ontwerp\">tinevanwel</a></p>");
        var facts = new LeadEvidenceService().Extract(new[] { await f.FetchAsync(Site, default) }!, Site);
        Assert.False(facts.LinkedIns.Single(l => l.Handle == "klant-bv").Trusted);
        Assert.True(facts.LinkedIns.Single(l => l.Handle == "ontwerpstudio-x").Trusted);     // in the footer
        Assert.True(facts.LinkedIns.Single(l => l.Handle == "tinevanwel-ontwerp").Trusted);  // resembles the domain

        var (result, lead) = await Claims(f, Json(new { companyLinkedInUrl = Claim("https://www.linkedin.com/company/klant-bv", "onze klant", Site) }));
        Assert.DoesNotContain("companyLinkedInUrl", result.Verified.Keys);
        Assert.Contains(result.Dropped, d => d.Field == "companyLinkedInUrl" && d.Reason.Contains("client"));
        Assert.Equal("", lead.LinkedInUrl);
        var det = await Deterministic(Pages("<p>Wij werken voor <a href=\"https://www.linkedin.com/company/klant-bv\">onze klant</a>.</p>"));
        Assert.DoesNotContain("companyLinkedInUrl", det.Verified.Keys);
    }

    // ---- 7: the cheap ones ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_footer_number_with_a_copyright_sign_is_not_a_credit_and_the_best_occurrence_of_a_number_counts()
    {
        var footer = Pages("<p>Welkom</p>", contact: "<p>Tel 06 144 70 482 © 2026 Tine van Wel</p>");
        var (ok, lead) = await Claims(footer, Json(new { ownerMobile = Claim("06 144 70 482", "Tel 06 144 70 482", Site + "contact") }));
        Assert.Equal("06 14 47 04 82", lead.OwnerMobile);

        // the same number first next to a credit, later in the contact block: the contact block wins
        var both = Pages("<p>Welkom</p>", contact: "<p>Bel mij op 06 144 70 482</p>", about: "<p>Website door Studio Pixel 06 144 70 482</p>");
        var facts = new LeadEvidenceService().Extract(new[] { await both.FetchAsync(Site + "over-mij", default), await both.FetchAsync(Site + "contact", default) }!, Site);
        var mobile = Assert.Single(facts.Mobiles);
        Assert.False(mobile.Credit);
        Assert.True(mobile.Contact);
    }

    [Theory]
    [InlineData("https://linktr.ee/tine", "https://linktr.ee/other", false)]
    [InlineData("https://linktr.ee/tine", "https://www.linktr.ee/Tine/", true)]
    [InlineData("https://medium.com/@tine", "https://medium.com/@someone", false)]
    [InlineData("https://tine.my.canva.site/", "https://other.my.canva.site/", false)]
    [InlineData("https://tine.canva.site/", "https://other.canva.site/", false)]
    [InlineData("https://bio.link/tine", "https://bio.link/other", false)]
    public void Hosts_that_many_businesses_share_are_told_apart_by_subdomain_or_path(string a, string b, bool same) =>
        Assert.Equal(same, UrlGuard.SiteKey(new Uri(a)) == UrlGuard.SiteKey(new Uri(b)));

    [Fact]
    public async Task A_site_that_is_shared_by_path_keeps_its_path_and_a_redirect_to_another_path_is_not_followed()
    {
        Assert.Equal("https://linktr.ee/tine", UrlGuard.CanonicalWebsite(new Uri("https://linktr.ee/tine/extra?x=1")));
        Assert.Equal("https://www.tinevanwel.nl", UrlGuard.CanonicalWebsite(new Uri("https://www.tinevanwel.nl/over?x=1")));
        Assert.Equal("linktr.ee/tine", UrlGuard.DomainOf("https://www.linktr.ee/Tine/x"));

        var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://linktr.ee/other");
        var fetcher = new SafePageFetcher(new HttpClient(new FuncHandler2(_ => redirect)));
        Assert.Null(await fetcher.FetchAsync("https://linktr.ee/tine", CancellationToken.None));

        // the listing of another linktr.ee page is not this business
        var listing = new GooglePlacesResult { GoogleRating = 5f, Website = "https://linktr.ee/other" };
        Assert.False(HuntService.ListingIsThisBusiness(listing, new Lead { Website = "https://linktr.ee/tine" }, new EvidenceFacts()));
        Assert.True(HuntService.ListingIsThisBusiness(new GooglePlacesResult { Website = "https://linktr.ee/tine" }, new Lead { Website = "https://linktr.ee/tine" }, new EvidenceFacts()));
    }

    private sealed class FuncHandler2 : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
        public FuncHandler2(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_send(request));
    }

    [Theory]
    [InlineData("janine@tinevanwel.nl", "Jan Jansen", false)]        // janine is not Jan
    [InlineData("jan@tinevanwel.nl", "Jan Jansen", true)]
    [InlineData("jan.jansen@tinevanwel.nl", "Jan Jansen", true)]
    [InlineData("janjansen@tinevanwel.nl", "Jan Jansen", true)]
    [InlineData("jjansen@tinevanwel.nl", "Jan Jansen", true)]
    [InlineData("jansenbakker@tinevanwel.nl", "Jan Jansen", false)]
    public async Task A_mailbox_belongs_to_a_person_by_the_exact_first_name_or_initial_and_surname(string address, string owner, bool tied)
    {
        var f = Pages($"<p>Ik ben {owner}, eigenaar</p>", contact: $"<p>Mail: {address}</p>");   // 'I am' is a strong cue (rank 3)
        var (result, _) = await Claims(f, Json(new { personalEmail = Claim(address, $"Mail: {address}", Site + "contact") }));
        Assert.Equal(tied, result.Verified.ContainsKey("personalEmail"));
    }

    [Fact]
    public async Task A_typed_value_that_equals_the_verified_one_stays_the_persons_and_clearing_a_name_clears_its_parts()
    {
        var f = Pages("<p>Tine van Wel, eigenaar</p>", contact: "<p>Bel mij op 06 144 70 482</p>");
        var result = await Deterministic(f);
        var scoring = new SalesScoreService();

        var typed = new Lead { Name = "Tine", Website = Site, OwnerMobile = "06 14 47 04 82", OwnerName = "Tine van Wel", GoogleRating = 5f };
        LeadVerifier.Apply(typed, result, clearUnverified: true, scoring, null, HuntService.MayClear(typed, false));
        Assert.False(HuntProvenance.Unchanged(typed, "ownerMobile"));   // the same value, but the person's own: never recorded as written by the server
        Assert.False(HuntProvenance.Unchanged(typed, "ownerName"));

        var written = new Lead { Name = "Tine", Website = Site, OwnerName = "Wrong Person", OwnerFirstName = "Wrong", OwnerLastName = "Person", OwnerTitle = "Eigenaar" };
        HuntProvenance.Record(written, new[] { "ownerName" });
        var gone = await Deterministic(Pages("<p>Welkom</p>"), "Wrong Person");
        LeadVerifier.Apply(written, gone, clearUnverified: true, scoring, null, HuntService.MayClear(written, false));
        Assert.Equal("", written.OwnerName);
        Assert.Equal("", written.OwnerFirstName);
        Assert.Equal("", written.OwnerLastName);
        Assert.Equal("", written.OwnerTitle);
    }
}
