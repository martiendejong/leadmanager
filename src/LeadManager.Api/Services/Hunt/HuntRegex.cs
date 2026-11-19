using System.Text.RegularExpressions;

namespace LeadManager.Api.Services.Hunt;

/// <summary>
/// Every regular expression that runs over text from a web page has a match timeout. A pattern that backtracks badly on a hostile page then
/// throws a <see cref="RegexMatchTimeoutException"/> after a second instead of holding a worker thread for minutes; callers treat that as
/// "no match" (see LeadEvidenceService.Extract). Use these instead of <c>new Regex</c> and the static <c>Regex.*</c> methods.
/// </summary>
public static class HuntRegex
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    public static Regex New(string pattern, RegexOptions options = RegexOptions.None) => new(pattern, options, Timeout);

    public static bool IsMatch(string input, string pattern, RegexOptions options = RegexOptions.None) => Regex.IsMatch(input, pattern, options, Timeout);

    public static Match Match(string input, string pattern, RegexOptions options = RegexOptions.None) => Regex.Match(input, pattern, options, Timeout);

    public static MatchCollection Matches(string input, string pattern, RegexOptions options = RegexOptions.None) => Regex.Matches(input, pattern, options, Timeout);

    public static string Replace(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None) => Regex.Replace(input, pattern, replacement, options, Timeout);

    public static string[] Split(string input, string pattern, RegexOptions options = RegexOptions.None) => Regex.Split(input, pattern, options, Timeout);
}
