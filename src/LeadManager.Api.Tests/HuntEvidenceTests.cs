using System.Net;
using LeadManager.Api.Services.Hunt;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>The deterministic part: every trap of the first manual hunt has a fixture.</summary>
public class HuntEvidenceTests
{
    private static FetchedPage Page(string url, string html) => new(url, 200, html, HtmlText.ToText(html));
    private static EvidenceFacts Extract(string html, string site = "https://example.nl/") => new LeadEvidenceService().Extract(new[] { Page(site, html) }, site);

    [Theory]
    [InlineData("06-24266295", "0624266295")]
    [InlineData("06 144 70 482", "0614470482")]
    [InlineData("+31 6 12 34 56 78", "0612345678")]
    [InlineData("+31 (0)6-12345678", "0612345678")]
    [InlineData("0031612345678", "0612345678")]
    [InlineData("06.12.34.56.78", "0612345678")]
    public void Mobile_numbers_are_recognised_in_every_common_notation(string raw, string expected) =>
        Assert.Equal(expected, PhoneNumbers.NormalizeMobile(raw));

    [Theory]
    [InlineData("010 206 12 34")]   // contains "06" but is a Rotterdam landline: the old rule counted it as mobile
    [InlineData("020-6061234")]
    [InlineData("085 123 45 67")]
    [InlineData("088 123 45 67")]
    [InlineData("0800 0606")]
    [InlineData("0900 0666")]
    [InlineData("06 1234")]
    [InlineData("")]
    public void Landlines_and_service_numbers_are_not_mobile(string raw) => Assert.False(PhoneNumbers.IsDutchMobile(raw));

    [Fact]
    public void A_landline_next_to_a_mobile_is_kept_apart()
    {
        var f = Extract("<p>Kantoor 010 206 12 34, mobiel 06 12 34 56 78</p>");
        Assert.Single(f.Mobiles);
        Assert.Equal("0612345678", f.Mobiles[0].Normalized);
        Assert.NotEmpty(f.Landlines);
    }

    [Fact]
    public void A_mobile_number_in_a_tel_link_is_found()
    {
        var f = Extract("<a href=\"tel:+31612345678\">Bel mij</a>");
        Assert.Contains(f.Mobiles, m => m.Normalized == "0612345678");
    }

    [Fact]
    public void Mailto_that_shows_one_address_and_opens_another_is_flagged()
    {
        var f = Extract("<a href=\"mailto:agnes.goldenbeld@online.nl\">agnes@goldenbeldadvies.nl</a>", "https://goldenbeldadvies.nl/");
        var opened = f.Emails.Single(e => e.Address == "agnes.goldenbeld@online.nl");
        var shown = f.Emails.Single(e => e.Address == "agnes@goldenbeldadvies.nl");
        Assert.True(opened.FreeProvider);
        Assert.StartsWith("The link shows", opened.Warning);
        Assert.StartsWith("The link shows", shown.Warning);
    }

    [Fact]
    public void A_link_without_mailto_prefix_is_found_with_a_warning_but_the_same_address()
    {
        var f = Extract("<a href=\"tine@tinevanwel.nl\">tine@tinevanwel.nl</a>", "https://tinevanwel.nl/");
        var e = Assert.Single(f.Emails);
        Assert.Equal("tine@tinevanwel.nl", e.Address);
        Assert.Contains("no mailto", e.Warning);
        Assert.True(e.SameDomain);
        Assert.False(e.Role);
    }

    [Theory]
    [InlineData("info@bedrijf.nl")]
    [InlineData("contact@bedrijf.nl")]
    [InlineData("kantoor@bedrijf.nl")]
    [InlineData("administratie@bedrijf.nl")]
    public void Role_mailboxes_are_flagged(string address)
    {
        var f = Extract($"<p>Mail ons: {address}</p>");
        Assert.True(Assert.Single(f.Emails).Role);
    }

    [Fact]
    public void Image_file_names_that_look_like_addresses_are_ignored() =>
        Assert.Empty(Extract("<img src=\"logo@2x.png\"><p>logo@2x.png</p>").Emails);

    [Fact]
    public void Footer_copyright_range_is_never_a_founding_year()
    {
        var f = Extract("<p>In 2010 ben ik met Goldenbeld Advies gestart.</p><footer>© 2020 - 2026 Goldenbeld Advies</footer>");
        var founded = f.Years.Single(y => y.Kind == "Founded");
        Assert.Equal(2010, founded.Year);
        Assert.All(f.Years.Where(y => y.Kind == "Copyright"), y => Assert.Equal(2020, y.Year));
        Assert.DoesNotContain(f.Years, y => y.Kind != "Copyright" && y.Year == 2020);
    }

    [Fact]
    public void A_year_in_the_owners_career_is_marked_as_such()
    {
        var f = Extract("<p>Sinds 2006 werk ik als zelfstandig communicatieadviseur.</p>");
        Assert.True(Assert.Single(f.Years).CareerLike);
    }

    [Fact]
    public void Since_and_founded_phrases_give_years()
    {
        Assert.Equal(2009, Assert.Single(Extract("<p>Sinds 2009 helpen wij ondernemers.</p>").Years).Year);
        Assert.Equal(2012, Assert.Single(Extract("<p>Het bedrijf is opgericht in 2012.</p>").Years).Year);
        Assert.Equal(2015, Extract("<p>In 2015 ben ik mijn eigen bedrijf gestart.</p>").Years.Single().Year);
    }

    [Fact]
    public void Legal_form_in_the_footer_contradicts_small()
    {
        var f = Extract("<p>Ik werk zelfstandig.</p><footer>Augustin Advocatuur B.V. - KvK 12345678</footer>");
        Assert.Contains(f.LegalForms, l => l.Kind == "bv");
        Assert.Contains(f.LegalForms, l => l.Kind == "zelfstandig");
        Assert.True(LeadEvidenceService.ContradictsSmall(f));
        Assert.Equal("12345678", Assert.Single(f.Kvk).Number);
    }

    [Fact]
    public void A_sole_proprietor_does_not_contradict_small()
    {
        var f = Extract("<p>Mijn eenmanszaak is ingeschreven bij de KvK onder nummer 09186133.</p>");
        Assert.Contains(f.LegalForms, l => l.Kind == "eenmanszaak");
        Assert.False(LeadEvidenceService.ContradictsSmall(f));
    }

    [Theory]
    [InlineData("marijeweijers", "Marije Weijers", true)]
    [InlineData("jorn-bergmans-1a2b3c", "Jorn Bergmans", true)]
    [InlineData("drostanita", "Anita Drost", true)]
    [InlineData("denniskoetstra", "Dennis Koetstra", true)]
    [InlineData("sabobomediation", "Petra Delhez", false)]   // the handle of the business, not of the person
    [InlineData("richard-odijk-2893b019", "Richard Odijk", true)]
    [InlineData("jan-de-vries", "Jan de Vries", true)]
    [InlineData("petradelhez", "Petra", false)]              // a single name is not enough
    [InlineData("someone-else", "Anita Drost", false)]
    public void Handle_must_carry_the_owners_name(string handle, string owner, bool expected) =>
        Assert.Equal(expected, LeadEvidenceService.HandleCarriesName(handle, owner));

    [Theory]
    [InlineData("volledig zelfstandig met mijn eigen ontwerpbureau", true)]
    [InlineData("Mijn eenmanszaak", true)]
    [InlineData("ik werk alleen", true)]
    [InlineData("ACVD is één persoon", true)]
    [InlineData("Ik werk samen met zes zelfstandige collega's", true)]   // the phrase is there; the critic and ContradictsSmall decide the rest
    [InlineData("we werken met freelancers", false)]
    [InlineData("geen team op de site", false)]
    public void Small_needs_the_business_own_words(string quote, bool expected) =>
        Assert.Equal(expected, LeadEvidenceService.QuoteStatesSmall(quote));

    [Fact]
    public void Linkedin_personal_and_company_links_are_separated()
    {
        var f = Extract("<a href=\"https://www.linkedin.com/in/tinevanwel/\">in</a><a href=\"https://nl.linkedin.com/company/acme-bv\">co</a><a href=\"https://www.linkedin.com/in/share\">x</a>");
        Assert.Contains(f.LinkedIns, l => l.Kind == "in" && l.Handle == "tinevanwel");
        Assert.Contains(f.LinkedIns, l => l.Kind == "company" && l.Handle == "acme-bv");
        Assert.DoesNotContain(f.LinkedIns, l => l.Handle == "share");
    }

    // ---- SSRF guard --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]   // cloud metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("46.62.219.14", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_are_allowed(string ip, bool expected) =>
        Assert.Equal(expected, UrlGuard.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("ftp://example.nl/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:pass@example.nl/")]
    [InlineData("http://intranet/")]
    [InlineData("http://printer.local/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Unsafe_urls_are_rejected_before_any_request(string url) => Assert.False(UrlGuard.TryNormalize(url, out _));

    [Theory]
    [InlineData("tinevanwel.nl", "https://tinevanwel.nl/")]
    [InlineData("https://www.example.nl/over?x=1", "https://www.example.nl/over?x=1")]
    public void Public_urls_are_accepted(string url, string expected)
    {
        Assert.True(UrlGuard.TryNormalize(url, out var uri));
        Assert.Equal(expected, uri.ToString());
        Assert.Equal(UrlGuard.DomainOf(url), uri.Host.Replace("www.", ""));
    }
}
