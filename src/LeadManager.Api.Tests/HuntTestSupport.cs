using System.Text.Json;
using LeadManager.Api.Hubs;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Hunt;
using Microsoft.AspNetCore.SignalR;

namespace LeadManager.Api.Tests;

/// <summary>Serves pages from a dictionary: no network.</summary>
public class FakePageFetcher : IPageFetcher
{
    private readonly Dictionary<string, string> _pages = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Requested { get; } = new();
    /// <summary>Runs on every fetch: lets a test change the world (for example cancel the hunt) while pages are being read.</summary>
    public Action? OnFetch { get; set; }

    public FakePageFetcher Add(string url, string html) { _pages[Canon(url)] = html; return this; }

    private static string Canon(string url) => url.TrimEnd('/').Replace("://www.", "://").ToLowerInvariant();

    public Task<FetchedPage?> FetchAsync(string url, CancellationToken ct)
    {
        Requested.Add(url);
        OnFetch?.Invoke();
        if (!UrlGuard.TryNormalize(url, out var uri)) return Task.FromResult<FetchedPage?>(null);
        if (!_pages.TryGetValue(Canon(uri.ToString()), out var html)) return Task.FromResult<FetchedPage?>(new FetchedPage(uri.ToString(), 404, "", ""));
        return Task.FromResult<FetchedPage?>(new FetchedPage(uri.ToString(), 200, html, HtmlText.ToText(html)));
    }
}

/// <summary>Records what would have been sent to JengoWork; no network.</summary>
public class FakeDispatcher : IHuntDispatcher
{
    public FakeDispatcher(bool configured = true) { IsConfigured = configured; }
    public bool IsConfigured { get; }
    public bool Fail { get; set; }
    public List<(HuntJob Job, string Prompt)> Dispatched { get; } = new();

    public Task<DispatchResult> DispatchAsync(LeadManager.Api.Models.HuntJob job, string prompt, CancellationToken ct)
    {
        if (!IsConfigured) throw new DispatchNotConfiguredException();
        if (Fail) throw new HttpRequestException("JengoWork answered 500 when the agent task was created.");
        Dispatched.Add((job, prompt));
        return Task.FromResult(new DispatchResult("9001", "https://tasks.example.test/board/x/task/9001"));
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

/// <summary>Builds the claims JSON an agent submits, and the fixture sites it is checked against.</summary>
public static class HuntJson
{
    public static object Claim(string value, string quote, string source) => new { value, quote, source_url = source };

    public static string Json(object o) => JsonSerializer.Serialize(o);

    public static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

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
