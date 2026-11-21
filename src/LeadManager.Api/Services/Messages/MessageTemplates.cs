using System.Text;
using System.Text.RegularExpressions;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Hunt;

namespace LeadManager.Api.Services.Messages;

/// <summary>The values a template can use: only fields that are stored on the lead (verified by the hunt or typed by a person). Nothing is invented.</summary>
public record TemplateValues(string Name, string Owner, string Why, string Need, string Product, string Reason, string Language, string Benefit = "", string Sender = "");

public record BuiltInTemplate(string Channel, string Language, string Subject, string Body);

/// <summary>
/// The deterministic starting point of a message: a template per channel (and per product key when a person made one) with the placeholders
/// {name}, {owner}, {why}, {need}, {product} and {reason}. No AI and no key. A line whose placeholder has no value is left out, so a lead
/// without verified needs gets a short neutral message. The result is only a draft that the user edits and sends themselves.
/// </summary>
public static class MessageTemplates
{
    public static readonly string[] Placeholders = { "name", "owner", "why", "need", "product", "benefit", "sender", "reason" };

    private static readonly Regex Token = HuntRegex.New(@"\{([a-z]{2,10})\}", RegexOptions.IgnoreCase);
    private static readonly Regex AnyBrace = HuntRegex.New(@"\{([^{}\r\n]{1,40})\}");
    private static readonly Regex OnItsOwnSite = HuntRegex.New("On its own site: \"(.+?)\"\\.(?:\\s|$)");

    // ---- the built-in templates (client-facing text: Dutch for Dutch leads, English otherwise) --------------------------

    private static readonly List<BuiltInTemplate> BuiltIn = new()
    {
        new(MessageChannels.Email, "nl", "Kennismaken met {name}",
            "Hoi {owner},\n\nIk kwam {name} tegen en zou me graag kort aan u voorstellen.\nOp uw website lees ik: \"{need}\"\nOok viel mij op: \"{why}\"\n{benefit}\n\nMag ik u hier kort over bellen? Past een moment deze of volgende week?\n\nMet vriendelijke groet,\n{sender}"),
        new(MessageChannels.LinkedIn, "nl", "",
            "Hoi {owner},\nIk kwam {name} tegen.\nIk las op uw website: \"{need}\"\n{benefit}\nZullen we eens kort bellen?\n{sender}"),
        new(MessageChannels.WhatsApp, "nl", "",
            "Hoi {owner}! Ik kwam {name} tegen.\nOp jullie site staat: \"{need}\"\n{benefit}\nHeb je deze week een paar minuten om kort te bellen?\n{sender}"),
        new(MessageChannels.Phone, "nl", "",
            "Belnotitie {name}\nGesprekspartner: {owner}\nAanleiding: \"{why}\"\nBehoefte: \"{need}\"\nVoorstel: {product}\nVraag: past een korte kennismaking deze of volgende week?"),

        new(MessageChannels.Email, "en", "Getting to know {name}",
            "Hi {owner},\n\nI came across {name} and would like to introduce myself briefly.\nOn your website I read: \"{need}\"\nI also noticed: \"{why}\"\n{benefit}\n\nMay I call you briefly about this? Would a moment this week or next suit you?\n\nKind regards,\n{sender}"),
        new(MessageChannels.LinkedIn, "en", "",
            "Hi {owner},\nI came across {name}.\nI read on your website: \"{need}\"\n{benefit}\nShall we have a short call?\n{sender}"),
        new(MessageChannels.WhatsApp, "en", "",
            "Hi {owner}! I came across {name}.\nYour site says: \"{need}\"\n{benefit}\nDo you have a few minutes this week for a short call?\n{sender}"),
        new(MessageChannels.Phone, "en", "",
            "Call note {name}\nContact: {owner}\nReason: \"{why}\"\nNeed: \"{need}\"\nProposal: {product}\nQuestion: would a short introduction this week or next suit?"),
    };

    // English-first templates for the Kenya products (karibumara, farmmgmt, maaride): used instead of the language templates above.
    private static readonly List<BuiltInTemplate> KenyaBuiltIn = new()
    {
        new(MessageChannels.Email, "en", "An idea for {name}",
            "Hello {owner},\n\nI came across {name} and wanted to reach out briefly.\nOn your website I read: \"{need}\"\nI also noticed: \"{why}\"\n{benefit}\n\nWould you be open to a short call or a WhatsApp chat this week or next to see whether this is useful for you?\n\nKind regards,\n{sender}"),
        new(MessageChannels.LinkedIn, "en", "",
            "Hello {owner},\nI came across {name}.\nI read on your website: \"{need}\"\n{benefit}\nWould you be open to a short chat?\n{sender}"),
        new(MessageChannels.WhatsApp, "en", "",
            "Hello {owner}! I came across {name}.\nYour site says: \"{need}\"\n{benefit}\nDo you have a few minutes this week for a short chat?\n{sender}"),
        new(MessageChannels.Phone, "en", "",
            "Call note {name}\nContact: {owner}\nReason: \"{why}\"\nNeed: \"{need}\"\nProposal: {product}\nQuestion: would a short introduction this week or next suit? (offer a WhatsApp chat if a call is hard)"),
    };

    /// <summary>The built-in template for a channel and language; a Kenya product gets its own English template.</summary>
    public static BuiltInTemplate Get(string channel, string language, string? productKey) =>
        ProductCatalog.EnglishFirst.Contains(ProductCatalog.Canonical(productKey) ?? "") ? KenyaBuiltIn.First(t => t.Channel == channel) : Get(channel, language);

    public static IReadOnlyList<BuiltInTemplate> KenyaTemplates => KenyaBuiltIn;

    public static IReadOnlyList<BuiltInTemplate> All => BuiltIn;

    public static BuiltInTemplate Get(string channel, string language) =>
        BuiltIn.First(t => t.Channel == channel && t.Language == language);

    // ---- what each product does for the reader, written per language (the stored fit reason is English and is never pasted into Dutch text)

    private static readonly Dictionary<string, (string Nl, string En)> Benefits = new()
    {
        ["coachos"] = ("CoachOS geeft uw klanten een persoonlijke AI-gids en chat op uw eigen website, zodat vragen en intake sneller lopen.",
                       "CoachOS gives your clients a personal AI guide and chat on your own website, so questions and intake go faster."),
        ["siteaudit"] = ("Een maandelijkse Site audit laat zien wat er op uw website op het gebied van veiligheid, SEO en vindbaarheid door AI beter kan.",
                         "A monthly Site audit shows what can be improved on your website for security, SEO and findability by AI."),
        ["visit"] = ("Het Visit-platform maakt uw aanbod meertalig vindbaar en boekbaar voor bezoekers van de regio.",
                     "The Visit platform makes your offer findable and bookable in several languages for visitors of the region."),
        ["werkteam"] = ("Ons AI Werkteam neemt administratie en terugkerend werk over, voor een vaste prijs en met menselijke controle.",
                        "Our AI Work Team takes over admin and recurring work, for a fixed price and with human review."),
        ["cloudws"] = ("Met de Jengo Cloud Workspace krijgt u een eigen AI-werkplek, ook bereikbaar vanaf uw telefoon.",
                       "The Jengo Cloud Workspace gives you your own AI workspace, also reachable from your phone."),
        ["custom"] = ("Wij bouwen maatwerksoftware voor een proces, register of koppeling die geen standaardproduct dekt.",
                      "We build custom software for a process, register or integration that no standard product covers."),
        ["mail"] = ("JengoMail laat AI uw e-mail voorbereiden, zodat u alleen nog hoeft goed te keuren.",
                    "JengoMail lets AI prepare your e-mail, so you only have to approve."),
        ["meet"] = ("Jengo Meet maakt live aantekeningen van uw gesprekken en zet afspraken om in taken.",
                    "Jengo Meet takes live notes of your meetings and turns agreements into tasks."),
        ["karibumara"] = ("Het Karibu Mara-platform maakt uw aanbod meertalig vindbaar voor bezoekers van de Maasai Mara, met een reisplanner en een AI-gids waarmee gasten zelf hun reis plannen.",
                          "The Karibu Mara visit platform makes your offer findable in several languages for visitors to the Maasai Mara, with a trip planner and an AI guide chat so guests can plan their own trip."),
        ["farmmgmt"] = ("Het farm management dashboard houdt vee, voer en producten bij op een plek, met dagelijkse rapporten per e-mail en een website met aanvraagformulier voor kopers.",
                        "The farm management dashboard keeps your livestock, fodder and product records in one place, with daily reports by e-mail and a website with an enquiry form for buyers."),
        ["maaride"] = ("MaaLink Ride is een ritplatform voor een stad: app voor passagiers en chauffeurs, tariefberekening, controle van chauffeursdocumenten en een beheerdashboard.",
                       "MaaLink Ride is a ride-hailing platform for a town: rider and driver apps, a fare engine, driver document verification and an admin dashboard, so rides stop depending on phone calls and WhatsApp."),
    };

    public static string Benefit(string productKey, string language) =>
        Benefits.TryGetValue(productKey, out var b) ? (language == "nl" ? b.Nl : b.En) : "";

    // ---- language and values -------------------------------------------------------------------------------------------

    /// <summary>
    /// "nl" for a Dutch business (a .nl site or a Dutch phone number), otherwise "en". Always "en" when the product is one of the English-first
    /// Kenya products (the chosen <paramref name="productKey"/>, or else the first stored fit), or when the lead is Kenyan (+254 phone or a .ke site).
    /// </summary>
    public static string LanguageOf(Lead lead, string? productKey = null)
    {
        var key = ProductCatalog.Canonical(productKey)
            ?? LeadInsight.ParseFit(lead.ProductFitJson)?.Select(f => ProductCatalog.Canonical(f.Product)).FirstOrDefault(k => k != null);
        if (key != null && ProductCatalog.EnglishFirst.Contains(key)) return "en";
        if (HuntRegex.IsMatch(lead.Website ?? "", @"^(?:https?://)?[^/]*\.ke(?:[/:]|$)", RegexOptions.IgnoreCase)) return "en";
        foreach (var number in new[] { lead.Phone, lead.OwnerMobile })
            if (!string.IsNullOrWhiteSpace(number) && HuntRegex.IsMatch(number.Trim(), @"^(?:\+|00)[\s\-]*254")) return "en";
        if (Uri.TryCreate(lead.Website ?? "", UriKind.Absolute, out var u) && u.Host.EndsWith(".nl", StringComparison.OrdinalIgnoreCase)) return "nl";
        if (HuntRegex.IsMatch(lead.Website ?? "", @"^(?:https?://)?[^/]*\.nl(?:[/:]|$)", RegexOptions.IgnoreCase)) return "nl";
        foreach (var number in new[] { lead.Phone, lead.OwnerMobile })
            if (!string.IsNullOrWhiteSpace(number) && HuntRegex.IsMatch(number.Trim(), @"^(?:\+31|0031|0)[\s\-]*[1-9]")) return "nl";
        return "en";
    }

    private static string Clip(string s, int max)
    {
        s = HuntRegex.Replace(s ?? "", @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..(max - 3)].TrimEnd() + "...";
    }

    /// <summary>The first need line split into its quote (the business's own words) and the label.</summary>
    private static (string Label, string Quote) FirstNeed(string? needs)
    {
        // Only a need with a quote counts: the quote is the business's own words (in their language). A label without a quote is the
        // agent's text (English), which must never end up in a message to the business.
        foreach (var raw in (needs ?? "").Split('\n'))
        {
            var m = HuntRegex.Match(raw.Trim(), "^(.*?): \"(.*)\"$");
            if (m.Success && m.Groups[2].Value.Trim().Length > 0) return (m.Groups[1].Value, m.Groups[2].Value);
        }
        return ("", "");
    }

    /// <summary>
    /// The values from what is stored on the lead. {why} is the verified quote of the observation (never the internal score text), {need} the
    /// quote of the first need (the business's own words), {product} the name of the chosen product, {reason} the reason stored with the fit.
    /// </summary>
    public static TemplateValues ValuesFor(Lead lead, string? productKey, string? sender = null)
    {
        var language = LanguageOf(lead, productKey);
        var owner = !string.IsNullOrWhiteSpace(lead.OwnerFirstName) ? lead.OwnerFirstName.Trim()
            : (lead.OwnerName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        var why = "";
        if (!string.IsNullOrWhiteSpace(lead.WhyLead))
        {
            var matches = OnItsOwnSite.Matches(lead.WhyLead);
            if (matches.Count > 0) why = Clip(matches[^1].Groups[1].Value, 200);
        }

        var need = Clip(FirstNeed(lead.Needs).Quote, 200);
        // A need that is only a label (typed by a person, no quote) is the person's own text: it can be used as it is.
        var fits = LeadInsight.ParseFit(lead.ProductFitJson) ?? new List<ProductFitEntry>();
        var fit = !string.IsNullOrWhiteSpace(productKey)
            ? fits.FirstOrDefault(f => string.Equals(f.Product, productKey, StringComparison.OrdinalIgnoreCase))
            : fits.FirstOrDefault(f => ProductCatalog.Canonical(f.Product) != null);
        var key = ProductCatalog.Canonical(productKey) ?? (fit != null ? ProductCatalog.Canonical(fit.Product) : null);
        var product = key == null ? "" : ProductCatalog.All.FirstOrDefault(p => p.Key == key)?.Name ?? "";
        return new TemplateValues(Clip(lead.Name ?? "", 120), owner, why, need, product, fit == null ? "" : Clip(fit.Reason, 300), language,
            key == null ? "" : Benefit(key, language), Clip(sender ?? "", 80));
    }

    // ---- rendering -------------------------------------------------------------------------------------------------------

    private static string Value(TemplateValues v, string placeholder) => placeholder.ToLowerInvariant() switch
    {
        "name" => v.Name, "owner" => v.Owner, "why" => v.Why, "need" => v.Need, "product" => v.Product, "reason" => v.Reason,
        "benefit" => v.Benefit, "sender" => v.Sender, _ => "",
    };

    private static readonly Regex GreetingStart = HuntRegex.New(@"^\s*(?:Hoi|Beste|Hi|Dear|Hallo|Hello)\b", RegexOptions.IgnoreCase);
    private static readonly Regex GreetingWord = HuntRegex.New(@"\b(?:Hoi|Beste|Hi|Dear|Hallo|Hello)\s*(?=[!,.:]|$)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Fills in the placeholders. A line with a placeholder that has no value is left out (so nothing is invented and nothing reads "{need}").
    /// The one exception is a greeting whose only missing value is {owner}: it becomes a neutral greeting ("Goedendag,", "Hello,").
    /// </summary>
    public static string RenderBody(string template, TemplateValues v)
    {
        var lines = new List<string>();
        foreach (var raw in (template ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var tokens = Token.Matches(raw).Cast<Match>().Where(m => Placeholders.Contains(m.Groups[1].Value.ToLowerInvariant())).ToList();
            if (tokens.Count == 0) { lines.Add(raw); continue; }
            var empty = tokens.Where(t => string.IsNullOrWhiteSpace(Value(v, t.Groups[1].Value))).Select(t => t.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
            string Fill(string text) => Token.Replace(text, m => Placeholders.Contains(m.Groups[1].Value.ToLowerInvariant()) ? Value(v, m.Groups[1].Value) : m.Value);

            if (empty.Count == 0) { lines.Add(Fill(raw)); continue; }
            if (empty.Count == 1 && empty[0] == "owner" && GreetingStart.IsMatch(raw))
            {
                var greeting = Fill(raw);
                greeting = GreetingWord.Replace(greeting, v.Language == "nl" ? "Goedendag" : "Hello", 1);
                lines.Add(HuntRegex.Replace(greeting, @"\s+([!,.:])", "$1"));
                continue;
            }
            // any other line with a value missing is left out
        }
        // no stack of empty lines where lines were left out
        var text = string.Join("\n", lines);
        text = HuntRegex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public static string RenderSubject(string template, TemplateValues v)
    {
        var s = Token.Replace(template ?? "", m => Placeholders.Contains(m.Groups[1].Value.ToLowerInvariant()) ? Value(v, m.Groups[1].Value) : m.Value);
        s = HuntRegex.Replace(s, @"\s+", " ").Trim();
        return MessageRules.SingleLine(s, MessageRules.MaxSubject);
    }

    /// <summary>The unknown placeholders of a template text (a typo like {nam}), or an empty list.</summary>
    public static List<string> UnknownPlaceholders(string? text) =>
        AnyBrace.Matches(text ?? "").Cast<Match>().Select(m => m.Groups[1].Value)
            .Where(p => !Placeholders.Contains(p.ToLowerInvariant())).Distinct().ToList();

    /// <summary>Builds subject and body for a lead from the given template texts.</summary>
    public static (string Subject, string Body) Render(string subjectTemplate, string bodyTemplate, TemplateValues v) =>
        (RenderSubject(subjectTemplate, v), MessageRules.Body(RenderBody(bodyTemplate, v)));
}

/// <summary>Limits and clean-up of what is stored and shown: nothing in a subject can add a header, a body has a length and no control characters.</summary>
public static class MessageRules
{
    public const int MaxSubject = 200;
    public const int MaxBody = 5000;
    public const int MaxMailtoChars = 1900;

    /// <summary>One line: control characters and line breaks become spaces, white space is collapsed, clamped to <paramref name="max"/>.</summary>
    public static string SingleLine(string? s, int max)
    {
        var chars = (s ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var line = HuntRegex.Replace(new string(chars), @"\s+", " ").Trim();
        return line.Length > max ? line[..max].TrimEnd() : line;
    }

    /// <summary>A body: line breaks are kept (as \n), other control characters are removed.</summary>
    public static string Body(string? s)
    {
        var text = (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        return new string(text.Where(c => c == '\n' || c == '\t' || !char.IsControl(c)).ToArray()).Trim();
    }

    private static readonly Regex Address = HuntRegex.New(@"^[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}$");

    public static bool IsValidAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) && address.Length <= 254 && Address.IsMatch(address.Trim());

    /// <summary>
    /// The mailto link for an address the business published: strict address, a one-line subject (a line break in a subject could add a header in
    /// some mail apps), the body with CRLF line breaks, everything percent-encoded, and short enough for a mail app. Null when it cannot be built.
    /// </summary>
    public static string? Mailto(string? address, string? subject, string? body)
    {
        if (!IsValidAddress(address)) return null;
        var url = "mailto:" + Uri.EscapeDataString(address!.Trim()).Replace("%40", "@")
                  + "?subject=" + Uri.EscapeDataString(SingleLine(subject, MaxSubject))
                  + "&body=" + Uri.EscapeDataString(Body(body).Replace("\n", "\r\n"));
        return url.Length <= MaxMailtoChars ? url : null;
    }

    /// <summary>The lead's published address for e-mail: a personal one first, then the company address; null when neither is valid.</summary>
    public static string? AddressOf(Lead lead) =>
        new[] { lead.PersonalEmail, lead.CompanyEmail }.Select(a => (a ?? "").Trim()).FirstOrDefault(IsValidAddress);

    private static readonly Regex DoNotContact = HuntRegex.New(
        @"do[\s\-]?not[\s\-]?(?:contact|call|mail|e-?mail|disturb)|\bdnc\b|niet\s+(?:bellen|benaderen|mailen|e-?mailen|contacteren|storen)|bel[\s\-]?me[\s\-]?niet|geen\s+(?:acquisitie|reclame|sales)|opt[\s\-]?out|stop\s+(?:met\s+)?contact",
        RegexOptions.IgnoreCase);

    /// <summary>The excerpt of a note that says the lead must not be contacted, or null. (There is no do-not-contact field, so the notes are read.)</summary>
    public static string? DoNotContactNote(IEnumerable<string> notes)
    {
        foreach (var note in notes)
        {
            var m = DoNotContact.Match(note ?? "");
            if (!m.Success) continue;
            var start = Math.Max(0, m.Index - 40);
            return SingleLine(note![start..Math.Min(note.Length, m.Index + m.Length + 80)], 160);
        }
        return null;
    }
}
