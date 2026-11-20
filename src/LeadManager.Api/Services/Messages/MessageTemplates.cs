using System.Text;
using System.Text.RegularExpressions;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Hunt;

namespace LeadManager.Api.Services.Messages;

/// <summary>The values a template can use: only fields that are stored on the lead (verified by the hunt or typed by a person). Nothing is invented.</summary>
public record TemplateValues(string Name, string Owner, string Why, string Need, string Product, string Reason, string Language);

public record BuiltInTemplate(string Channel, string Language, string Subject, string Body);

/// <summary>
/// The deterministic starting point of a message: a template per channel (and per product key when a person made one) with the placeholders
/// {name}, {owner}, {why}, {need}, {product} and {reason}. No AI and no key. A line whose placeholder has no value is left out, so a lead
/// without verified needs gets a short neutral message. The result is only a draft that the user edits and sends themselves.
/// </summary>
public static class MessageTemplates
{
    public static readonly string[] Placeholders = { "name", "owner", "why", "need", "product", "reason" };

    private static readonly Regex Token = HuntRegex.New(@"\{([a-z]{2,10})\}", RegexOptions.IgnoreCase);
    private static readonly Regex AnyBrace = HuntRegex.New(@"\{([^{}\r\n]{1,40})\}");
    private static readonly Regex OnItsOwnSite = HuntRegex.New("On its own site: \"(.+?)\"\\.(?:\\s|$)");

    // ---- the built-in templates (client-facing text: Dutch for Dutch leads, English otherwise) --------------------------

    private static readonly List<BuiltInTemplate> BuiltIn = new()
    {
        new(MessageChannels.Email, "nl", "Kennismaken met {name}",
            "Hoi {owner},\n\nIk kwam {name} tegen en zou me graag kort aan u voorstellen.\nOp uw website lees ik: \"{need}\"\nOok viel mij op: \"{why}\"\nDaarom denk ik dat {product} u kan helpen.\n\nMag ik u hier kort over bellen? Past een moment deze of volgende week?\n\nMet vriendelijke groet,"),
        new(MessageChannels.LinkedIn, "nl", "",
            "Hoi {owner},\nIk kwam {name} tegen.\nIk las op uw website: \"{need}\"\nDaar kan {product} bij helpen.\nZullen we eens kort bellen?"),
        new(MessageChannels.WhatsApp, "nl", "",
            "Hoi {owner}! Ik kwam {name} tegen.\nOp jullie site staat: \"{need}\"\nDaar kan {product} bij helpen.\nHeb je deze week een paar minuten om kort te bellen?"),
        new(MessageChannels.Phone, "nl", "",
            "Belnotitie {name}\nGesprekspartner: {owner}\nAanleiding: \"{why}\"\nBehoefte: \"{need}\"\nVoorstel: {product}\nVraag: past een korte kennismaking deze of volgende week?"),

        new(MessageChannels.Email, "en", "Getting to know {name}",
            "Hi {owner},\n\nI came across {name} and would like to introduce myself briefly.\nOn your website I read: \"{need}\"\nI also noticed: \"{why}\"\nThat is why I think {product} could help you.\n\nMay I call you briefly about this? Would a moment this week or next suit you?\n\nKind regards,"),
        new(MessageChannels.LinkedIn, "en", "",
            "Hi {owner},\nI came across {name}.\nI read on your website: \"{need}\"\n{product} could help with that.\nShall we have a short call?"),
        new(MessageChannels.WhatsApp, "en", "",
            "Hi {owner}! I came across {name}.\nYour site says: \"{need}\"\n{product} could help with that.\nDo you have a few minutes this week for a short call?"),
        new(MessageChannels.Phone, "en", "",
            "Call note {name}\nContact: {owner}\nReason: \"{why}\"\nNeed: \"{need}\"\nProposal: {product}\nQuestion: would a short introduction this week or next suit?"),
    };

    public static IReadOnlyList<BuiltInTemplate> All => BuiltIn;

    public static BuiltInTemplate Get(string channel, string language) =>
        BuiltIn.First(t => t.Channel == channel && t.Language == language);

    // ---- language and values -------------------------------------------------------------------------------------------

    /// <summary>"nl" for a Dutch business (a .nl site or a Dutch phone number), otherwise "en".</summary>
    public static string LanguageOf(Lead lead)
    {
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
        var line = (needs ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var m = HuntRegex.Match(line, "^(.*?): \"(.*)\"$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : ("", line);
    }

    /// <summary>
    /// The values from what is stored on the lead. {why} is the verified quote of the observation (never the internal score text), {need} the
    /// quote of the first need (the business's own words), {product} the name of the chosen product, {reason} the reason stored with the fit.
    /// </summary>
    public static TemplateValues ValuesFor(Lead lead, string? productKey)
    {
        var language = LanguageOf(lead);
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
        return new TemplateValues(Clip(lead.Name ?? "", 120), owner, why, need, product, fit == null ? "" : Clip(fit.Reason, 300), language);
    }

    // ---- rendering -------------------------------------------------------------------------------------------------------

    private static string Value(TemplateValues v, string placeholder) => placeholder.ToLowerInvariant() switch
    {
        "name" => v.Name, "owner" => v.Owner, "why" => v.Why, "need" => v.Need, "product" => v.Product, "reason" => v.Reason, _ => "",
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
