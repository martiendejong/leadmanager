using System.Text.RegularExpressions;
using LeadManager.Api.Services.Hunt;

namespace LeadManager.Api.Services.Compose;

/// <summary>Checks on what goes into the model (the user's instruction) and on what comes out of it. Output is never trusted.</summary>
public static class AiGuards
{
    public const int MaxInstruction = 500;

    /// <summary>
    /// The instruction of the user: control characters and markup characters removed, white space collapsed. It is NOT filtered for
    /// "orders" (it is an order by nature: "shorter, more informal"); instead it is placed below the rules and cannot override them.
    /// Null = fine. Longer than <see cref="MaxInstruction"/> is refused, not cut.
    /// </summary>
    public static string? TryClean(string? raw, out string clean)
    {
        clean = "";
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Length > MaxInstruction * 2) return $"The instruction is at most {MaxInstruction} characters.";
        var chars = raw.Select(c => char.IsControl(c) || c == '<' || c == '>' ? ' ' : c).ToArray();
        var cleaned = HuntRegex.Replace(new string(chars), @"\s+", " ").Trim();
        if (cleaned.Length > MaxInstruction) return $"The instruction is at most {MaxInstruction} characters.";
        clean = cleaned;
        return null;
    }

    /// <summary>Text the user typed earlier (the editor): line breaks kept, control and markup characters removed, clamped.</summary>
    public static string EditorText(string? raw, int max)
    {
        var t = new string((raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n')
            .Select(c => c == '<' || c == '>' ? ' ' : c).Where(c => c == '\n' || c == '\t' || !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    private static readonly Regex DashBetweenDigits = HuntRegex.New(@"(?<=\d)\s*[–—]\s*(?=\d)");
    private static readonly Regex SpacedDash = HuntRegex.New(@"\s*[–—]\s*");

    /// <summary>No em or en dashes ever: between digits a hyphen, elsewhere a comma.</summary>
    public static string NoDashes(string? text)
    {
        var t = DashBetweenDigits.Replace(text ?? "", "-");
        return SpacedDash.Replace(t, ", ").Replace(", ,", ",").Replace(",,", ",");
    }

    private static readonly Regex Price = HuntRegex.New(
        @"(?:€|\bEUR\b|\beuro'?s?\b|\bUSD\b|\$)\s*\d|\d\s*(?:€|\beuro'?s?\b|\bEUR\b|\bUSD\b)|\buurtarief\b|\bhourly\s+(?:rate|fee|price)\b|\bper\s+(?:uur|hour)\b|/\s*(?:uur|hour|hr)\b|\bday\s+rate\b|\bdagtarief\b",
        RegexOptions.IgnoreCase);

    /// <summary>A price or rate in the text (never allowed: the internal hourly rate must not leak, and nothing may promise a price).</summary>
    public static bool HasPrice(string? text) => Price.IsMatch(text ?? "");

    private static readonly Regex Link = HuntRegex.New(
        @"(?:https?://|www\.)[^\s<>""')\]]+|[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.IgnoreCase);

    /// <summary>The first link or e-mail address in the text that does not appear in the facts, or null.</summary>
    public static string? UnknownLink(string? text, string facts)
    {
        foreach (Match m in Link.Matches(text ?? ""))
        {
            var v = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (!facts.Contains(v, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    public static string Clip(string? s, int max)
    {
        s = HuntRegex.Replace(s ?? "", @"[ \t]+", " ").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }
}
