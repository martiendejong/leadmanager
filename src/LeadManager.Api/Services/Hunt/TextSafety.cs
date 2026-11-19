using System.Text.RegularExpressions;

namespace LeadManager.Api.Services.Hunt;

/// <summary>
/// Free text that comes from a web page or from an agent is untrusted data. It is shown to people and, later, put into prompts of
/// language models. This keeps it short, free of markup and control characters, and free of phrases that try to give instructions.
/// </summary>
public static class TextSafety
{
    private static readonly Regex Instruction = new(
        @"(?:ignore\s+(?:all|any|the|your)?\s*(?:previous|prior|above|earlier)[^.]{0,60}|disregard[^.]{0,60}|forget\s+(?:all|everything|your)[^.]{0,60}|system\s+prompt|you\s+are\s+now|act\s+as\s+(?:a|an|the)\b[^.]{0,40}|\b(?:system|assistant|developer)\s*:|negeer\s+(?:alle|de|je)\s+(?:vorige|eerdere|instructies)[^.]{0,60}|nieuwe\s+instructies?|reveal\s+(?:the|your)\s+(?:prompt|instructions)[^.]{0,40})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the text contains a line break, markup characters or an instruction-like phrase (a segment or region must not).</summary>
    public static bool Suspicious(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        return raw.Any(c => c is '\r' or '\n' or '<' or '>' or '`' || char.IsControl(c)) || Instruction.IsMatch(raw);
    }

    /// <summary>Control characters, markup characters and instruction-like phrases removed, white space collapsed, clamped to max characters.</summary>
    public static string Clean(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var kept = new string(raw.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).Where(c => c is not ('<' or '>' or '`')).ToArray());
        kept = Instruction.Replace(kept, "[removed]");
        kept = Regex.Replace(kept, @"\s+", " ").Trim();
        return kept.Length > max ? kept[..max].TrimEnd() : kept;
    }
}
