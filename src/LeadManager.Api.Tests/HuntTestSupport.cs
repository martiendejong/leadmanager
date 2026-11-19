using System.Text.Json;
using LeadManager.Api.Hubs;
using LeadManager.Api.Services.Hunt;
using Microsoft.AspNetCore.SignalR;

namespace LeadManager.Api.Tests;

/// <summary>Serves pages from a dictionary: no network.</summary>
public class FakePageFetcher : IPageFetcher
{
    private readonly Dictionary<string, string> _pages = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Requested { get; } = new();

    public FakePageFetcher Add(string url, string html) { _pages[Canon(url)] = html; return this; }

    private static string Canon(string url) => url.TrimEnd('/').Replace("://www.", "://").ToLowerInvariant();

    public Task<FetchedPage?> FetchAsync(string url, CancellationToken ct)
    {
        Requested.Add(url);
        if (!UrlGuard.TryNormalize(url, out var uri)) return Task.FromResult<FetchedPage?>(null);
        if (!_pages.TryGetValue(Canon(uri.ToString()), out var html)) return Task.FromResult<FetchedPage?>(new FetchedPage(uri.ToString(), 404, "", ""));
        return Task.FromResult<FetchedPage?>(new FetchedPage(uri.ToString(), 200, html, HtmlText.ToText(html)));
    }
}

/// <summary>A language model that answers from a script: the first call is the extractor, the second the critic.</summary>
public class FakeLlm : ILeadLlm
{
    private readonly Func<string, string, string> _answer;
    public FakeLlm(Func<string, string, string> answer, bool configured = true) { _answer = answer; IsConfigured = configured; }
    public bool IsConfigured { get; }
    public string ModelName => "fake";
    public int Calls { get; private set; }
    public List<string> Prompts { get; } = new();

    public Task<string> CompleteJsonAsync(string system, string user, HuntBudget budget, CancellationToken ct)
    {
        if (!IsConfigured) throw new AiNotConfiguredException();
        budget.Consume();
        Calls++;
        Prompts.Add(user);
        return Task.FromResult(_answer(system, user));
    }

    public static bool IsCritic(string system) => system.Contains("very critical fact checker");
}

public class FakeDiscovery : ILeadDiscovery
{
    private readonly Queue<IReadOnlyList<DiscoveredCompany>> _rounds;
    public FakeDiscovery(params IReadOnlyList<DiscoveredCompany>[] rounds) => _rounds = new Queue<IReadOnlyList<DiscoveredCompany>>(rounds);
    public bool IsConfigured => true;
    public int Rounds { get; private set; }
    public List<IReadOnlyCollection<string>> Excluded { get; } = new();

    public Task<IReadOnlyList<DiscoveredCompany>> DiscoverAsync(string segment, string region, IReadOnlyCollection<string> excludeDomains, int count, HuntBudget budget, CancellationToken ct)
    {
        budget.Consume();
        Rounds++;
        Excluded.Add(excludeDomains.ToList());
        return Task.FromResult(_rounds.Count > 0 ? _rounds.Dequeue() : (IReadOnlyList<DiscoveredCompany>)Array.Empty<DiscoveredCompany>());
    }
}

public class NoopHub : IHubContext<EnrichmentHub>
{
    public IHubClients Clients { get; } = new NoopClients();
    public IGroupManager Groups => throw new NotSupportedException();

    private class NoopClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NoopProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private class NoopProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>Builds the JSON the extractor and the critic answer with, from the fixture sites.</summary>
public static class HuntJson
{
    public static object Claim(string value, string quote, string source) => new { value, quote, source_url = source };

    public static string Json(object o) => JsonSerializer.Serialize(o);

    public static string CriticVerifiesAll(string user, params string[] except)
    {
        // The critic user message carries the claims as JSON; verify every field named in it except the given ones.
        var start = user.IndexOf("CLAIMS TO CHECK:", StringComparison.Ordinal);
        var json = user[(start + "CLAIMS TO CHECK:".Length)..];
        json = json[..json.IndexOf("\n\nPAGES:", StringComparison.Ordinal)];
        using var doc = JsonDocument.Parse(json);
        var verdicts = new Dictionary<string, object>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var field = c.GetProperty("field").GetString()!;
            verdicts[field] = except.Contains(field) ? new { status = "unverified", reason = "Not convincing." } : new { status = "verified", reason = "Clear in the quote." };
        }
        return Json(new { verdicts });
    }

    public const string Site = "https://www.tinevanwel.nl/";

    /// <summary>A small design studio that earns an honest 8 without a Google rating.</summary>
    public static FakePageFetcher GoodSite()
    {
        var f = new FakePageFetcher();
        f.Add(Site, "<html><body><nav><a href=\"/contact\">Contact</a> <a href=\"/over-mij\">Over mij</a></nav>" +
                    "<p>Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau.</p>" +
                    "<a href=\"https://www.linkedin.com/in/tinevanwel/\">LinkedIn</a></body></html>");
        f.Add(Site + "contact", "<html><body><h1>Contact</h1><p>Bel mij op 06 144 70 482</p><p>Mail: tine@tinevanwel.nl</p></body></html>");
        f.Add(Site + "over-mij", "<html><body><p>In 2015 ben ik mijn eigen bedrijf gestart: Grafisch ontwerpbureau Tine van Wel.</p></body></html>");
        return f;
    }

    public static string GoodExtraction() => Json(new
    {
        ownerName = Claim("Tine van Wel", "Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau", Site),
        ownerLinkedInUrl = Claim("https://www.linkedin.com/in/tinevanwel/", "LinkedIn", Site),
        ownerMobile = Claim("06 144 70 482", "Bel mij op 06 144 70 482", Site + "contact"),
        personalEmail = Claim("tine@tinevanwel.nl", "Mail: tine@tinevanwel.nl", Site + "contact"),
        foundingYear = Claim("2015", "In 2015 ben ik mijn eigen bedrijf gestart", Site + "over-mij"),
        smallCompany = Claim("1", "volledig zelfstandig met mijn eigen ontwerpbureau", Site),
        description = "One person graphic design studio.",
    });
}
