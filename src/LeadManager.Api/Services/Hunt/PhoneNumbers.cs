using System.Text.RegularExpressions;

namespace LeadManager.Api.Services.Hunt;

/// <summary>Dutch phone number rules shared by the score and the evidence extraction.</summary>
public static class PhoneNumbers
{
    // 06 or +31 6 followed by eight digits, with spaces, dots, dashes or a "(0)" in between.
    private const string Pattern = @"(?<![\d])(?:\+\s?31|0031|0)[\s\-\.\(\)]*(?:0[\s\-\.\)]*)?6(?:[\s\-\.]*\d){8}(?![\d])";
    private static readonly Regex Candidate = HuntRegex.New(Pattern, RegexOptions.Compiled);
    private static readonly Regex Whole = HuntRegex.New(@"^\s*" + Pattern + @"\s*$", RegexOptions.Compiled);

    /// <summary>"06 12 34 56 78" or "+31 (0)6-12345678" become "0612345678"; anything else (landline, 085, 088, 0800, 0900) is null.</summary>
    public static string? NormalizeMobile(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Candidate.Match(raw);
        return m.Success ? Digits(m.Value) : null;
    }

    /// <summary>
    /// Like <see cref="NormalizeMobile"/> but the WHOLE text must be one mobile number: "0612345678 and some text" is not a number.
    /// Used for values that are stored from a claim.
    /// </summary>
    public static string? NormalizeMobileStrict(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 40) return null;
        var m = Whole.Match(raw);
        return m.Success ? Digits(raw.Trim()) : null;
    }

    private static string? Digits(string written)
    {
        var digits = new string(written.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0031")) digits = digits[4..];
        else if (written.TrimStart().StartsWith("+")) digits = digits[2..];
        else if (digits.StartsWith("0")) digits = digits[1..];
        if (digits.StartsWith("0")) digits = digits[1..]; // the optional "(0)" after +31
        return digits.Length == 9 && digits[0] == '6' ? "0" + digits : null;
    }

    /// <summary>"0612345678" becomes "06 12 34 56 78".</summary>
    public static string Format(string normalized) =>
        normalized.Length == 10 ? $"{normalized[..2]} {normalized[2..4]} {normalized[4..6]} {normalized[6..8]} {normalized[8..]}" : normalized;

    public static bool IsDutchMobile(string? raw) => NormalizeMobile(raw) != null;

    /// <summary>Like <see cref="FindMobiles"/>, with the position of each number in the text (no second search of the whole text per number).</summary>
    public static IEnumerable<(string Normalized, string AsWritten, int Index)> FindMobilesWithIndex(string text)
    {
        foreach (Match m in Candidate.Matches(text ?? ""))
        {
            var n = NormalizeMobile(m.Value);
            if (n != null) yield return (n, m.Value.Trim(), m.Index + (m.Value.Length - m.Value.TrimStart().Length));
        }
    }

    /// <summary>All mobile numbers in a text, normalised, with the exact text they were written as.</summary>
    public static IEnumerable<(string Normalized, string AsWritten)> FindMobiles(string text)
    {
        foreach (Match m in Candidate.Matches(text ?? ""))
        {
            var n = NormalizeMobile(m.Value);
            if (n != null) yield return (n, m.Value.Trim());
        }
    }
}
