using System.Text.RegularExpressions;

namespace LeadManager.Api.Services.Hunt;

/// <summary>Dutch phone number rules shared by the score and the evidence extraction.</summary>
public static class PhoneNumbers
{
    // 06 or +31 6 followed by eight digits, with spaces, dots, dashes or a "(0)" in between.
    private static readonly Regex Candidate = new(
        @"(?<![\d])(?:\+\s?31|0031|0)[\s\-\.\(\)]*(?:0[\s\-\.\)]*)?6(?:[\s\-\.]*\d){8}(?![\d])",
        RegexOptions.Compiled);

    /// <summary>"06 12 34 56 78" or "+31 (0)6-12345678" become "0612345678"; anything else (landline, 085, 088, 0800, 0900) is null.</summary>
    public static string? NormalizeMobile(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Candidate.Match(raw);
        if (!m.Success) return null;
        var digits = new string(m.Value.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0031")) digits = digits[4..];
        else if (m.Value.TrimStart().StartsWith("+")) digits = digits[2..];
        else if (digits.StartsWith("0")) digits = digits[1..];
        if (digits.StartsWith("0")) digits = digits[1..]; // the optional "(0)" after +31
        return digits.Length == 9 && digits[0] == '6' ? "0" + digits : null;
    }

    public static bool IsDutchMobile(string? raw) => NormalizeMobile(raw) != null;

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
