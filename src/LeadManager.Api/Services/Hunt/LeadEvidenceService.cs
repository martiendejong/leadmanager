using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeadManager.Api.Services.Hunt;

/// <summary>A mobile number on a page. Contact: it stands in a contact context. Credit: it stands next to a review, a testimonial or a web designer's credit.</summary>
public record MobileFact(string Normalized, string AsWritten, string SourceUrl, string Quote, bool Contact = false, bool Credit = false);
public record LinkedInFact(string Kind, string Url, string Handle, string SourceUrl);
public record EmailFact(string Address, bool FromMailto, bool Role, bool FreeProvider, bool SameDomain, string SourceUrl, string Quote,
    /// <summary>The visible text of the link shows another address than the link opens (or the link is not a mailto at all).</summary>
    string? Warning);
public record LegalFact(string Kind, string SourceUrl, string Quote);
public record YearFact(int Year, string Kind, bool CareerLike, string SourceUrl, string Quote);
public record KvkFact(string Number, string SourceUrl, string Quote);
/// <summary>A person named on the site WITH a role cue (owner, founder, director, 'my name is', JSON-LD founder, about heading).</summary>
/// <summary>Rank: how strong the cue is (0 structured data, 1 about heading, 2 'my name is', 3 'I am', 4 and 5 name next to a role).</summary>
public record OwnerFact(string Name, string Cue, string SourceUrl, string Quote, int Rank = 9);
/// <summary>A first-person or legal statement that the business is a one-person business (not a plural, a negation or a third person).</summary>
public record SmallFact(string Kind, string SourceUrl, string Quote);

/// <summary>What the pages of one website show, found with plain rules and no language model.</summary>
public class EvidenceFacts
{
    public List<MobileFact> Mobiles { get; } = new();
    public List<LinkedInFact> LinkedIns { get; } = new();
    public List<EmailFact> Emails { get; } = new();
    public List<LegalFact> LegalForms { get; } = new();
    public List<YearFact> Years { get; } = new();
    public List<KvkFact> Kvk { get; } = new();
    public List<string> Landlines { get; } = new();
    public List<OwnerFact> Owners { get; } = new();
    public List<SmallFact> Small { get; } = new();
    /// <summary>The numbers of tel: links, digits and a leading plus only.</summary>
    public List<string> TelNumbers { get; } = new();
}

/// <summary>
/// Deterministic fact extraction. It knows the traps of the first manual hunt: a landline is not a mobile number, a link can
/// show one address and open another, a footer copyright range is not a founding year, a LinkedIn handle without the
/// owner's name proves nothing, and a B.V. in the footer means the business is not a sole proprietorship.
/// </summary>
public class LeadEvidenceService
{
    private static readonly HashSet<string> RoleMailboxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "info", "contact", "kantoor", "administratie", "support", "sales", "hallo", "hello", "mail", "post", "receptie", "secretariaat",
        "office", "service", "welkom", "boekhouding", "factuur", "facturen", "orders", "bestellingen", "reserveringen", "reservering",
        "webmaster", "noreply", "no-reply", "klantenservice", "verkoop", "inkoop", "praktijk", "team", "algemeen", "afspraak", "afspraken",
        "nl", "be", "contactus", "enquiries", "admin", "hr", "jobs", "vacatures", "pers", "press", "marketing", "finance", "debiteuren",
    };

    private static readonly HashSet<string> FreeProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "hotmail.com", "hotmail.nl", "outlook.com", "outlook.nl", "live.nl", "live.com", "yahoo.com", "yahoo.nl",
        "icloud.com", "me.com", "online.nl", "ziggo.nl", "kpnmail.nl", "home.nl", "planet.nl", "xs4all.nl", "hetnet.nl", "casema.nl",
        "chello.nl", "telfort.nl", "tele2.nl", "msn.com", "proton.me", "protonmail.com", "mail.com", "zonnet.nl", "upcmail.nl", "kpn.nl",
    };

    private static readonly HashSet<string> NotATld = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "svg", "webp", "css", "js", "ico", "woff", "woff2", "ttf", "map", "pdf", "mp4",
    };

    private static readonly Regex EmailRegex = new(@"[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex LinkedInRegex = new(@"(?<![A-Za-z0-9=?&#%/.\-_])https?://(?:[a-z]{2,3}\.|www\.)?linkedin\.com/(in|company)/([A-Za-z0-9\-_%.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ContactCue = new(@"\b(?:bel|bellen|tel|telefoon|mobiel|mobile|gsm|whatsapp|bereikbaar|bereik|contact|afspraak|call|phone)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CreditCue = new(@"(?:©|\bwebsite (?:door|by)\b|\bwebdesign\b|\bgemaakt door\b|\bontwerp door\b|\bpowered by\b|\brealisatie\b|\breview|\breferentie|\baanbeveling|\btestimonial|\bschreef\b|\bervaring(?:en)? van\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TelHref = new(@"href\s*=\s*[""']tel:([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KvkRegex = new(@"\bkvk(?:[\s\-]*(?:nummer|nr\.?|number))?[\s:\-#]*(\d{8})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LandlineRegex = new(@"(?<![\d])0(?:[1-5]|[7-9])\d(?:[\s\-\.]*\d){7}(?![\d])", RegexOptions.Compiled);

    private static readonly (string Kind, Regex Pattern)[] LegalPatterns =
    {
        ("eenmanszaak", new Regex(@"\beenmanszaak\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("zzp", new Regex(@"\bzzp(?:'er|er)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("zelfstandig", new Regex(@"\bzelfstandig(?:e)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("alleen", new Regex(@"\b(?:ik werk (?:graag )?alleen|één persoon|een[- ]persoons|one[- ]person|solo[- ]?(?:ondernemer|onderneemster))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("vof", new Regex(@"\b(?:v\.o\.f\.?|vof)\b", RegexOptions.Compiled)),
        ("bv", new Regex(@"(?<!\w)(?:B\.V\.|BV|b\.v\.|Besloten Vennootschap)(?!\w)", RegexOptions.Compiled)),
        ("nv", new Regex(@"(?<!\w)(?:N\.V\.|Naamloze Vennootschap)(?!\w)", RegexOptions.Compiled)),
        ("maatschap", new Regex(@"\bmaatschap\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("stichting", new Regex(@"\bstichting\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("team", new Regex(@"\b(?:ons team|mijn collega'?s|onze (?:medewerkers|collega'?s)|\d+ medewerkers)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static readonly Regex SinceRegex = new(@"\b(?:sinds|since|vanaf)\s+(?:het jaar\s+)?(?:\d{1,2}\s+\w+\s+)?(19[5-9]\d|20[0-2]\d)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FoundedRegex = new(@"\b(?:opgericht|gestart|begonnen|ontstaan|founded|established|oprichting|bestaat)\b[^.]{0,40}?\b(19[5-9]\d|20[0-2]\d)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InYearStartRegex = new(@"\bin\s+(19[5-9]\d|20[0-2]\d)\s+(?:ben ik|zijn wij|startte|starten|begon|begonnen|heb ik|hebben wij|ben|is)\b[^.]{0,80}?\b(?:start|opgericht|begon|gestart|oprichten|eigen bedrijf|onderneming|praktijk|bureau)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CopyrightRegex = new(@"(?:©|&copy;|copyright)\s*(?:(\d{4})\s*[-–—]\s*)?(\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BusinessCue = new(@"\b(?:bedrijf|praktijk|bureau|kantoor|onderneming|studio|zaak|atelier|opgericht|gestart|actief|bestaat|ondernem\w*|zelfstandig\w*|werk ik|werken wij|zijn wij|helpen wij|help ik|adviseer\w*|ontwerp\w*|bieden wij|bied ik)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>The sentence must be about a business ("het bedrijf is opgericht in"), not about a newsletter, a website or a course.</summary>
    private static readonly Regex BusinessNoun = new(@"\b(?:\w*(?:bureau|praktijk|kantoor|studio|atelier)|bedrijf|onderneming|zaak|firma|organisatie|eenmanszaak|zelfstandig\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotBusinessTopic = new(@"\b(?:nieuwsbrief|blog|podcast|webshop|website|site|campagne|cursus|training|workshop|actie|forum|vereniging|opleiding|studie|school|app|project)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>The owner (or "we") starting something is a start too: "In 2010 ben ik met Goldenbeld Advies gestart" has no business noun.</summary>
    private static readonly Regex OwnerStarted = new(@"\b(?:ik|wij|we)\b[^.]{0,80}?\b(?:gestart|begonnen|opgericht|startte|begon|oprichten|starten)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static bool FoundedCue(string sentence) =>
        (BusinessNoun.IsMatch(sentence) || OwnerStarted.IsMatch(sentence)) && !NotBusinessTopic.IsMatch(sentence) && !CopyrightLine.IsMatch(sentence);
    private static readonly Regex CopyrightLine = new(@"(?:©|&copy;|copyright|alle rechten|all rights|website (?:door|by)|webdesign|powered by)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CareerRegex = new(@"\b(?:werk ik als|werkzaam als|ervaring|carrière|loopbaan|werkte|werkte ik|in dienst|bij (?:de|het)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public EvidenceFacts Extract(IReadOnlyList<FetchedPage> pages, string? websiteUrl)
    {
        var facts = new EvidenceFacts();
        var siteDomain = UrlGuard.DomainOf(websiteUrl);
        foreach (var page in pages)
        {
            if (string.IsNullOrEmpty(page.Html) && string.IsNullOrEmpty(page.Text)) continue;
            ExtractMobiles(page, facts);
            ExtractLinkedIn(page, facts);
            ExtractEmails(page, siteDomain, facts);
            ExtractTextFacts(page, facts);
            ExtractOwners(page, facts);
            ExtractSmall(page, facts);
        }
        return facts;
    }

    /// <summary>The sentence a match stands in (between the neighbouring full stops), at most 300 characters.</summary>
    private static string SentenceAround(string text, int index, int length)
    {
        var start = text.LastIndexOfAny(new[] { '.', '!', '?', '©', '\n' }, Math.Max(0, index - 1)) + 1;
        var endAt = text.IndexOfAny(new[] { '.', '!', '?', '©', '\n' }, Math.Min(text.Length - 1, index + length));
        var end = endAt < 0 ? text.Length : endAt + 1;
        if (end - start > 300) { start = Math.Max(start, index - 150); end = Math.Min(end, index + length + 150); }
        return Regex.Replace(text.Substring(start, end - start), @"\s+", " ").Trim();
    }

    private static string Quote(string text, int index, int length, int pad = 90)
    {
        var start = Math.Max(0, index - pad);
        var end = Math.Min(text.Length, index + length + pad);
        return Regex.Replace(text.Substring(start, end - start), @"\s+", " ").Trim();
    }

    private static void ExtractMobiles(FetchedPage page, EvidenceFacts facts)
    {
        var seen = new HashSet<string>();
        var text = page.Text;
        var matches = PhoneRegexMatches(text);
        var contactPage = Regex.IsMatch(new Uri(page.Url).AbsolutePath, "contact|bereik|afspraak", RegexOptions.IgnoreCase);
        foreach (var (norm, written, index) in matches)
        {
            if (!seen.Add(norm)) continue;
            var quote = Quote(text, index, written.Length);
            facts.Mobiles.Add(new MobileFact(norm, written, page.Url, quote,
                Contact: contactPage || ContactCue.IsMatch(quote), Credit: CreditCue.IsMatch(quote)));
        }
        foreach (Match m in LandlineRegex.Matches(text)) facts.Landlines.Add(m.Value.Trim());
        foreach (Match m in TelHref.Matches(page.Html ?? ""))
        {
            var digits = Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), @"[^\d+]", "");
            if (digits.Length >= 9 && !facts.TelNumbers.Contains(digits)) facts.TelNumbers.Add(digits);
        }
    }

    private static List<(string Norm, string Written, int Index)> PhoneRegexMatches(string text)
    {
        var list = new List<(string, string, int)>();
        foreach (var (norm, written) in PhoneNumbers.FindMobiles(text))
        {
            var idx = text.IndexOf(written, StringComparison.Ordinal);
            list.Add((norm, written, Math.Max(idx, 0)));
        }
        return list;
    }

    private static void ExtractLinkedIn(FetchedPage page, EvidenceFacts facts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LinkedInRegex.Matches(WebUtility.HtmlDecode(page.Html)))
        {
            var kind = m.Groups[1].Value.ToLowerInvariant();
            var handle = m.Groups[2].Value.TrimEnd('/', '.');
            var url = $"https://www.linkedin.com/{kind}/{handle}/";
            if (kind == "in" && handle.Equals("share", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(kind + handle)) facts.LinkedIns.Add(new LinkedInFact(kind, url, handle, page.Url));
        }
    }

    private static bool PlausibleAddress(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 1) return false;
        var domain = address[(at + 1)..];
        var tld = domain[(domain.LastIndexOf('.') + 1)..];
        return !NotATld.Contains(tld) && !Regex.IsMatch(address, @"@\d+x\b|\d+x\.");
    }

    private static void ExtractEmails(FetchedPage page, string siteDomain, EvidenceFacts facts)
    {
        var index = new Dictionary<string, EmailFact>(StringComparer.OrdinalIgnoreCase);

        EmailFact Make(string address, bool mailto, string quote, string? warning)
        {
            var local = address[..address.IndexOf('@')];
            var domain = address[(address.IndexOf('@') + 1)..].ToLowerInvariant();
            var role = RoleMailboxes.Contains(local);
            var free = FreeProviders.Contains(domain);
            var same = siteDomain != "" && (domain == siteDomain || domain.EndsWith("." + siteDomain));
            return new EmailFact(address.ToLowerInvariant(), mailto, role, free, same, page.Url, quote, warning);
        }

        // Links first: they say where the address really goes.
        var doc = new HtmlDocument();
        doc.LoadHtml(page.Html ?? "");
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]")?.ToList() ?? new List<HtmlNode>())
        {
            var href = WebUtility.HtmlDecode(a.GetAttributeValue("href", "")).Trim();
            var shown = Regex.Replace(WebUtility.HtmlDecode(a.InnerText ?? ""), @"\s+", " ").Trim();
            string? target = null;
            string? warning = null;
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                var addr = Uri.UnescapeDataString(href[7..].Split('?')[0]).Split(',')[0].Trim();
                if (EmailRegex.IsMatch(addr) && PlausibleAddress(addr)) target = addr;
            }
            else if (EmailRegex.IsMatch(href) && !href.Contains("/") && PlausibleAddress(href))
            {
                // href="name@site.nl" without mailto: it does not open a mail window, but the address is the same.
                target = href;
                warning = "The link has no mailto: prefix, so it does not open a mail window.";
            }
            if (target == null) continue;

            var shownMail = EmailRegex.Match(shown);
            if (shownMail.Success && !shownMail.Value.Equals(target, StringComparison.OrdinalIgnoreCase))
                warning = $"The link shows {shownMail.Value} but opens {target}.";
            index[target] = Make(target, true, shown.Length > 0 ? shown : target, warning);
            if (shownMail.Success && !index.ContainsKey(shownMail.Value) && PlausibleAddress(shownMail.Value))
                index[shownMail.Value] = Make(shownMail.Value, false, shown, warning);
        }

        // Plain text addresses.
        foreach (Match m in EmailRegex.Matches(page.Text))
        {
            if (!PlausibleAddress(m.Value) || index.ContainsKey(m.Value)) continue;
            index[m.Value] = Make(m.Value, false, Quote(page.Text, m.Index, m.Length, 60), null);
        }
        facts.Emails.AddRange(index.Values);
    }

    private static void ExtractTextFacts(FetchedPage page, EvidenceFacts facts)
    {
        var text = page.Text;
        foreach (Match m in KvkRegex.Matches(text))
            if (!facts.Kvk.Any(k => k.Number == m.Groups[1].Value)) facts.Kvk.Add(new KvkFact(m.Groups[1].Value, page.Url, Quote(text, m.Index, m.Length)));

        foreach (var (kind, pattern) in LegalPatterns)
        {
            var m = pattern.Match(text);
            if (m.Success) facts.LegalForms.Add(new LegalFact(kind, page.Url, Quote(text, m.Index, m.Length, 70)));
        }

        void AddYear(Match m, string kind, int group)
        {
            var year = int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
            var quote = Quote(text, m.Index, m.Length, 70);
            var career = CareerRegex.IsMatch(quote);
            if (!facts.Years.Any(y => y.Year == year && y.Kind == kind && y.SourceUrl == page.Url))
                facts.Years.Add(new YearFact(year, kind, career, page.Url, quote));
        }
        foreach (Match m in SinceRegex.Matches(text))
        {
            // "sinds 2019" in a copyright line or a sentence that is not about the business (a membership, a hobby) is no founding year.
            var around = SentenceAround(text, m.Index, m.Length);
            if (CopyrightLine.IsMatch(around) || NotBusinessTopic.IsMatch(around) || !BusinessCue.IsMatch(around)) continue;
            AddYear(m, "Since", 1);
        }
        foreach (Match m in FoundedRegex.Matches(text))
            if (FoundedCue(SentenceAround(text, m.Index, m.Length))) AddYear(m, "Founded", 1);
        foreach (Match m in InYearStartRegex.Matches(text))
            if (FoundedCue(SentenceAround(text, m.Index, m.Length))) AddYear(m, "Founded", 1);
        foreach (Match m in CopyrightRegex.Matches(text))
        {
            var group = m.Groups[1].Success ? 1 : 2; // the first year of a range "2020 - 2026" is when the site started, never the business
            AddYear(m, "Copyright", group);
        }
    }

    // ---- owners ---------------------------------------------------------------------------------------------

    private const string NamePattern = @"[A-ZÀ-ÖØ-Þ][\p{L}'’\-]+(?:\s+(?:van|de|der|den|het|ten|ter|te|von|op|aan)){0,2}(?:\s+[A-ZÀ-ÖØ-Þ][\p{L}'’\-]+){1,2}";
    private const string RoleCue = @"(?:eigenaar|eigenares|oprichter|oprichtster|directeur|directrice|founder|co-?founder|owner|zaakvoerder|bedrijfsleider|ondernemer)";
    private static readonly Regex OwnerMyNameIs = new(@"(?i:mijn naam is|ik heet|my name is)\s+(?<n>" + NamePattern + ")", RegexOptions.Compiled);
    private static readonly Regex OwnerIAm = new(@"(?i:ik ben|i am)\s+(?<n>" + NamePattern + @")(?<rest>[^.!?]{0,160})", RegexOptions.Compiled);
    private static readonly Regex OwnerIAmContext = new(@"\b(?:mijn|onze|my)\s+(?:eigen\s+)?(?:bedrijf|praktijk|bureau|kantoor|onderneming|studio|zaak|atelier|company|practice)\b|\b(?:eigenaar|oprichter|owner|founder|zelfstandig\w*|eenmanszaak)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OwnerNameThenRole = new(@"(?<n>" + NamePattern + @")\s*[,\-–—|:(]\s*(?:[\p{L}& ]{0,25}?)(?i:" + RoleCue + ")", RegexOptions.Compiled);
    private static readonly Regex OwnerRoleThenName = new(@"(?i:" + RoleCue + @")\s*[:\-–—]?\s*(?<n>" + NamePattern + ")", RegexOptions.Compiled);
    private static readonly Regex AboutPath = new(@"/(?:over|about|wie|team|mij|ik|praktijk|bureau)(?:[-/_]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RoleWord = new(@"\b" + RoleCue + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly char[] QuoteMarks = { '"', '“', '”', '„', '‟', '«', '»', '‘', '’' };
    private static readonly Regex SignatureDash = new(@"[!?.""”»]\s*[-–—]\s*$", RegexOptions.Compiled);

    private static readonly HashSet<string> NotANameWord = new(StringComparer.OrdinalIgnoreCase)
    { "contact", "bel", "mail", "neem", "over", "home", "welkom", "meer", "lees", "klik", "diensten", "mijn", "onze", "ons", "het", "een", "ontwerp", "tevreden", "blij", "ervaring", "praktijk", "bureau", "studio", "team", "gratis" };

    /// <summary>Words that make a capitalised pair an organisation or a place, not a person: "Gemeente Zwolle", "Bakkerij Boer", "Boer B.V.".</summary>
    private static readonly Regex OrganisationOrPlace = new(
        @"^(?:gemeente|provincie|stichting|vereniging|bakkerij|slagerij|kapsalon|restaurant|hotel|café|cafe|holding|groep|bv|b\.v\.|vof|v\.o\.f\.|nv|n\.v\.|advocaten|notarissen|accountants|adviseurs|adviesbureau|"
        + @"zwolle|amsterdam|rotterdam|utrecht|haag|groningen|eindhoven|tilburg|almere|breda|nijmegen|apeldoorn|arnhem|haarlem|enschede|amersfoort|maastricht|leiden|dordrecht|zoetermeer|deventer|"
        + @"nederland|holland|drenthe|friesland|gelderland|overijssel|limburg|brabant|zeeland|flevoland|benelux|europa|international|nederlandse|koninklijke|ministerie|universiteit|hogeschool|ziekenhuis|school)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? CleanName(string raw)
    {
        var name = Regex.Replace(raw.Trim(' ', ',', '.', '-', '–', '—'), @"\s+", " ");
        if (name.Length is < 5 or > 60) return null;
        var tokens = name.Split(' ');
        if (tokens.Length is < 2 or > 5) return null;
        if (tokens.Any(t => NotANameWord.Contains(t) || OrganisationOrPlace.IsMatch(t))) return null;
        return name;
    }

    /// <summary>A name in a testimonial signature ("Geweldig werk! - Marieke de Boer, directeur Bakkerij Boer"), a quotation or a credit is not the owner.</summary>
    private static bool LooksLikeTestimonialOrCredit(string text, Match m, string quote)
    {
        if (CreditCue.IsMatch(quote) || quote.IndexOfAny(QuoteMarks) >= 0) return true;
        var before = text[Math.Max(0, m.Index - 8)..m.Index];
        return SignatureDash.IsMatch(before);
    }

    private static void AddOwner(EvidenceFacts facts, string raw, string cue, int rank, FetchedPage page, string quote)
    {
        var name = CleanName(raw);
        if (name == null) return;
        var key = NameKey(name);
        var existing = facts.Owners.FindIndex(o => NameKey(o.Name) == key && o.SourceUrl == page.Url);
        if (existing >= 0)
        {
            // The same person with a stronger cue (structured data beats a heading beats a sentence) replaces the weaker fact.
            if (rank < facts.Owners[existing].Rank) facts.Owners[existing] = new OwnerFact(name, cue, page.Url, quote, rank);
            return;
        }
        facts.Owners.Add(new OwnerFact(name, cue, page.Url, quote, rank));
    }

    /// <summary>The role word next to an owner's name ("eigenaar" gives "Eigenaar"), or null.</summary>
    public static string? RoleFromQuote(string? quote)
    {
        var m = RoleWord.Match(quote ?? "");
        return m.Success ? char.ToUpperInvariant(m.Value[0]) + m.Value[1..].ToLowerInvariant() : null;
    }

    private static void ExtractOwners(FetchedPage page, EvidenceFacts facts)
    {
        var text = page.Text;
        // Each block of the page (a heading, a paragraph, a list item) on its own: a name never runs from one block into the next.
        void Scan(Regex regex, string cue, int rank, int pad, Func<Match, bool>? accept = null)
        {
            foreach (var line in text.Split('\n'))
                foreach (Match m in regex.Matches(line))
                {
                    if (accept != null && !accept(m)) continue;
                    var quote = Quote(line, m.Index, m.Length, pad);
                    if (LooksLikeTestimonialOrCredit(line, m, quote)) continue;
                    AddOwner(facts, m.Groups["n"].Value, cue, rank, page, quote);
                }
        }
        Scan(OwnerMyNameIs, "my name is", 2, 70);
        Scan(OwnerIAm, "I am, with the business", 3, 30, m => OwnerIAmContext.IsMatch(m.Value));
        Scan(OwnerNameThenRole, "name and role", 4, 40);
        Scan(OwnerRoleThenName, "role and name", 5, 40);

        var doc = new HtmlDocument();
        doc.LoadHtml(page.Html ?? "");
        // An about page heading that is just a person's name.
        if (AboutPath.IsMatch(new Uri(page.Url).AbsolutePath))
            foreach (var h in doc.DocumentNode.SelectNodes("//h1|//h2")?.ToList() ?? new List<HtmlNode>())
            {
                var t = Regex.Replace(WebUtility.HtmlDecode(h.InnerText ?? ""), @"\s+", " ").Trim();
                if (Regex.IsMatch(t, "^" + NamePattern + "$")) AddOwner(facts, t, "about page heading", 1, page, t);
            }
        // JSON-LD: a Person that is the founder or owner, or that has a role cue as job title. The strongest cue.
        foreach (var script in doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']")?.ToList() ?? new List<HtmlNode>())
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(WebUtility.HtmlDecode(script.InnerText ?? ""));
                WalkJsonLd(json.RootElement, false, page, facts, 0);
            }
            catch (System.Text.Json.JsonException) { /* not valid JSON-LD: ignore */ }
        }
    }

    private static void WalkJsonLd(System.Text.Json.JsonElement el, bool roleParent, FetchedPage page, EvidenceFacts facts, int depth)
    {
        if (depth > 6) return;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray()) WalkJsonLd(item, roleParent, page, facts, depth + 1);
            return;
        }
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        var isPerson = el.TryGetProperty("@type", out var type) && type.ToString().Contains("Person", StringComparison.OrdinalIgnoreCase);
        if (isPerson && el.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var job = el.TryGetProperty("jobTitle", out var jt) ? jt.ToString() : "";
            if (roleParent || Regex.IsMatch(job, RoleCue, RegexOptions.IgnoreCase))
                AddOwner(facts, name.GetString() ?? "", "structured data: founder or owner", 0, page, name.GetString() ?? "");
        }
        foreach (var prop in el.EnumerateObject())
        {
            var role = prop.Name.Equals("founder", StringComparison.OrdinalIgnoreCase) || prop.Name.Equals("founders", StringComparison.OrdinalIgnoreCase)
                       || prop.Name.Equals("owner", StringComparison.OrdinalIgnoreCase);
            WalkJsonLd(prop.Value, role, page, facts, depth + 1);
        }
    }

    // ---- "small" ----------------------------------------------------------------------------------------------

    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+|\n", RegexOptions.Compiled);
    private static readonly Regex FirstPerson = new(@"\b(?:ik|mijn|mij|me|mezelf|ikzelf|i|my)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Negation = new(@"\b(?:geen|niet|nooit|zonder|not|no)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PluralOrCustomers = new(@"\b(?:zzp['’]?ers|zzp-ers|zelfstandigen|eenmanszaken)\b|\b(?:voor|aan|bij|helpen|klanten|opdrachtgevers|cliënten|reviews?|testimonial|aanbevelingen?|ervaring van)\b[^.]{0,30}\b(?:zzp|zelfstandig|eenmanszaak)|\b(?:met|van|bij|door|via|voor|aan|tegen)\s+(?:(?:mijn|onze|een|de|deze|die|uw|jouw|je)\s+)?(?:\w+\s+)?(?:zzp|zelfstandig|eenmanszaak)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>Somebody else is the subject: a husband, a brother, pupils, clients. "Ik ben blij dat mijn man zzp'er is" is not about the owner.</summary>
    private static readonly Regex OtherSubject = new(@"\b(?:mijn|onze)\s+(?:man|vrouw|broer|zus|zoon|dochter|vader|moeder|partner|collega|collega's|klant|klanten|leerlingen?|kinderen|kind|vriend|vriendin|buurman|ouders)\b|\b(?:hem|haar|hen|leerlingen?|kinderen|kind|cliënten|klanten)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The phrase must be the predicate of the owner: "ik ben ... zelfstandig/zzp'er", "ik werk alleen", "ik werk als zelfstandig ...", "mijn eenmanszaak", or a labelled legal form.
    private static readonly Regex SmallIkBen = new(@"\bik ben\b(?<mid>[^.!?]{0,80}?)\b(?:zzp['’]?er|zelfstandig(?:e)?|eenmanszaak)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SmallIkWerk = new(@"\bik werk (?:graag )?alleen\b|\bik werk als (?:zelfstandig\w*|zzp\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SmallMijnEenmanszaak = new(@"\bmijn\s+(?:eigen\s+)?eenmanszaak\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SmallLegalLine = new(@"\b(?:rechtsvorm|juridische vorm|bedrijfsvorm|legal form)\b\s*[:\-–]?\s*[^.]{0,30}\beenmanszaak\b|\bkvk\b[^.]{0,60}\beenmanszaak\b|\beenmanszaak\b[^.]{0,60}\bkvk\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SmallOnePerson = new(@"\b(?:één persoon|een[- ]persoons|one[- ]person|solo[- ]?(?:ondernemer|onderneemster))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsOnePerson = new(@"\bis\s+(?:één|een)\s+persoon\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A statement counts only when the OWNER says it about the business: the phrase is the predicate of "ik ben" or "ik werk", it is
    /// "mijn eenmanszaak", or it is a labelled legal form. A plural, a negation, a sentence about customers, a testimonial or about
    /// somebody else ("Mijn man is zzp'er", "Ik leer kinderen zelfstandig lezen") never counts.
    /// </summary>
    private static void ExtractSmall(FetchedPage page, EvidenceFacts facts)
    {
        foreach (var raw in SentenceSplit.Split(page.Text))
        {
            var sentence = raw.Trim();
            if (sentence.Length < 8 || sentence.Length > 400) continue;
            if (PluralOrCustomers.IsMatch(sentence)) continue;

            string? kind = null;
            Match? hit = null;
            Match m;
            if ((m = SmallLegalLine.Match(sentence)).Success) { kind = "eenmanszaak"; hit = m; }
            else if ((m = SmallMijnEenmanszaak.Match(sentence)).Success) { kind = "eenmanszaak"; hit = m; }
            else if ((m = SmallIkBen.Match(sentence)).Success && !OtherSubject.IsMatch(m.Groups["mid"].Value)) { kind = m.Value.Contains("eenmanszaak", StringComparison.OrdinalIgnoreCase) ? "eenmanszaak" : "self-employed"; hit = m; }
            else if ((m = SmallIkWerk.Match(sentence)).Success) { kind = "self-employed"; hit = m; }
            else if ((m = SmallOnePerson.Match(sentence)).Success && FirstPerson.IsMatch(sentence) && !OtherSubject.IsMatch(sentence)) { kind = "one-person"; hit = m; }
            else if ((m = IsOnePerson.Match(sentence)).Success && !OtherSubject.IsMatch(sentence)) { kind = "one-person"; hit = m; }
            if (hit == null) continue;

            // A negation in front of the phrase turns it around ("ik ben geen zzp'er").
            var before = sentence[..hit.Index];
            if (Negation.IsMatch(before.Length > 40 ? before[^40..] : before) || Negation.IsMatch(hit.Value)) continue;
            if (!facts.Small.Any(f => f.SourceUrl == page.Url && f.Quote == sentence)) facts.Small.Add(new SmallFact(kind!, page.Url, sentence));
        }
    }

    // ---- name checks used by the verification --------------------------------------------------------------

    private static readonly HashSet<string> NameParticles = new(StringComparer.OrdinalIgnoreCase)
    { "van", "de", "der", "den", "het", "ten", "ter", "te", "vd", "v", "d", "von", "la", "le", "el", "al" };

    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>An order- and particle-insensitive key of a person's name: "Tine van Wel" and "Wel, Tine" give the same key.</summary>
    public static string NameKey(string name)
    {
        var tokens = name.Split(new[] { ' ', '-', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !NameParticles.Contains(t)).Select(Fold).Where(t => t.Length > 0).OrderBy(t => t, StringComparer.Ordinal);
        return string.Join(" ", tokens);
    }

    /// <summary>The given name of a person ("Tine van Wel" gives "tine"), folded.</summary>
    public static string FirstName(string name)
    {
        var first = name.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => !NameParticles.Contains(t));
        return first == null ? "" : Fold(first);
    }

    /// <summary>The surname of a person: the last token that is not a particle, folded.</summary>
    public static string LastName(string name)
    {
        var last = name.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault(t => !NameParticles.Contains(t));
        return last == null ? "" : Fold(last);
    }

    /// <summary>
    /// Does a LinkedIn handle carry the owner's WHOLE name (first and last name)? "marijeweijers" yes, "weijers" no, "mweijers" no,
    /// "sabobomediation" for Petra Delhez no.
    /// </summary>
    public static bool HandleCarriesName(string handle, string? ownerName)
    {
        if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(ownerName)) return false;
        var folded = Fold(Uri.UnescapeDataString(handle));
        var first = FirstName(ownerName);
        var last = LastName(ownerName);
        if (first.Length < 2 || last.Length < 3 || first == last) return false;
        return folded.Contains(first) && folded.Contains(last);
    }

    private static readonly Regex SmallPhrase = new(
        @"\b(eenmanszaak|zzp(?:'er|er)?|zelfstandig(?:e)?\b|ik werk (?:graag )?alleen|één persoon|een[- ]persoons|one[- ]person|solo[- ]?ondernemer)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A quote counts as the business's own statement that it is small only when it carries one of these phrases.</summary>
    public static bool QuoteStatesSmall(string? quote) => !string.IsNullOrWhiteSpace(quote) && SmallPhrase.IsMatch(quote);

    /// <summary>True when the pages call the business a B.V., N.V., maatschap, stichting or name a team: it cannot count as small on a 'zelfstandig' remark.</summary>
    public static bool ContradictsSmall(EvidenceFacts facts) =>
        facts.LegalForms.Any(f => f.Kind is "bv" or "nv" or "maatschap" or "stichting" or "vof" or "team");
}
