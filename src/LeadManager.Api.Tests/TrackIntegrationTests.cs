using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JengoTrack;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Track;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>
/// JengoWork task 8071: Jengo Track integration. Webhook receiver (signature, idempotency, hot threshold),
/// timeline proxy and tracked-link helper, plus one test against the real Node implementation.
/// </summary>
public class TrackIntegrationTests : IDisposable
{
    private const string Me = "user-me";
    private const string Secret = "unit-test-webhook-secret-0123456789";

    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;

    public TrackIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _access = new CrmParentAccess(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static TrackOptions Options(bool enabled = true, int threshold = 2) => new()
    {
        Enabled = enabled,
        BaseUrl = "http://track.invalid",
        ApiKey = "unit-test-key",
        WebhookSecret = Secret,
        HotThreshold = threshold,
    };

    private TrackWebhookController Webhook(TrackOptions options, string body, string? signature)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (signature != null) ctx.Request.Headers["X-Track-Signature"] = signature;
        return new TrackWebhookController(_db, Options(options), NullLogger<TrackWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
    }

    private static IOptions<TrackOptions> Options(TrackOptions o) => Microsoft.Extensions.Options.Options.Create(o);

    private static string Sign(string body, string secret = Secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private static string ClickBody(Guid entityId, string entityType = "lead", int total = 1, long ts = 1_800_000_000_000,
        string token = "tok1", string label = "Offerte bekijken") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["event"] = "link.clicked",
            ["ts"] = ts,
            ["entity_type"] = entityType,
            ["entity_id"] = entityId.ToString(),
            ["entity_label"] = "Hogenkamp",
            ["campaign"] = "koude-acquisitie",
            ["source_tool"] = "leadmanager",
            ["channel"] = "mail",
            ["label"] = label,
            ["url"] = "https://example.com/offerte",
            ["token"] = token,
            ["device"] = "desktop",
            ["total_human_clicks"] = total,
        });

    private Lead SeedLead(string name = "Hogenkamp")
    {
        var lead = new Lead { Name = name, Website = "https://www.hogenkamp.nl", ImportedByUserId = Me };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        return lead;
    }

    private Client SeedClient(string name = "ThuisNotaris")
    {
        var client = new Client { Name = name, CreatedByUserId = Me };
        _db.Clients.Add(client);
        _db.SaveChanges();
        return client;
    }

    private async Task<IActionResult> Post(TrackOptions options, string body, string? signature = "auto")
    {
        var sig = signature == "auto" ? Sign(body) : signature;
        return await Webhook(options, body, sig).Receive(CancellationToken.None);
    }

    private static int Status(IActionResult r) => r switch
    {
        StatusCodeResult s => s.StatusCode,
        ObjectResult o => o.StatusCode ?? 200,
        _ => throw new Xunit.Sdk.XunitException($"Unexpected result {r.GetType().Name}"),
    };

    // ---- Signature -------------------------------------------------------------------

    [Fact]
    public async Task Webhook_GoodSignature_IsAccepted_AndLogsInteraction()
    {
        var lead = SeedLead();
        var body = ClickBody(lead.Id);

        var result = await Post(Options(), body);

        Assert.Equal(200, Status(result));
        var i = Assert.Single(_db.Interactions.Where(x => x.LeadId == lead.Id));
        Assert.Equal("Klikte op Offerte bekijken\nKanaal: mail · Campagne: koude-acquisitie · Bron: leadmanager", i.Summary);
        Assert.Equal(InteractionDirection.Inbound, i.Direction);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000).UtcDateTime, i.OccurredAt);
    }

    [Fact]
    public async Task Webhook_BadSignature_Is401_AndWritesNothing()
    {
        var lead = SeedLead();
        var body = ClickBody(lead.Id);

        Assert.Equal(401, Status(await Post(Options(), body, Sign(body, "another-secret-another-secret-12345"))));
        Assert.Equal(401, Status(await Post(Options(), body, "sha256=deadbeef")));
        Assert.Equal(401, Status(await Post(Options(), body, Sign(body + " "))));
        Assert.Empty(_db.Interactions);
    }

    [Fact]
    public async Task Webhook_MissingSignature_Is401()
    {
        var lead = SeedLead();
        Assert.Equal(401, Status(await Post(Options(), ClickBody(lead.Id), signature: null)));
        Assert.Equal(401, Status(await Post(Options(), ClickBody(lead.Id), signature: "")));
        Assert.Empty(_db.Interactions);
    }

    [Fact]
    public async Task Webhook_Disabled_Is503_EvenWithValidSignature()
    {
        var lead = SeedLead();
        Assert.Equal(503, Status(await Post(Options(enabled: false), ClickBody(lead.Id))));
        Assert.Empty(_db.Interactions);

        var noSecret = Options();
        noSecret.WebhookSecret = "";
        Assert.Equal(503, Status(await Post(noSecret, ClickBody(lead.Id), signature: "sha256=00")));
    }

    // ---- Entities --------------------------------------------------------------------

    [Fact]
    public async Task Webhook_UnknownEntity_And_OtherTypes_Are200_AndIgnored()
    {
        SeedLead();
        Assert.Equal(200, Status(await Post(Options(), ClickBody(Guid.NewGuid()))));                    // unknown id
        Assert.Equal(200, Status(await Post(Options(), ClickBody(Guid.NewGuid(), "contact"))));         // other type
        var notGuid = ClickBody(Guid.NewGuid()).Replace("\"entity_id\":\"", "\"entity_id\":\"42\",\"x\":\"");
        Assert.Equal(200, Status(await Post(Options(), notGuid)));                                      // numeric id
        Assert.Empty(_db.Interactions);
        Assert.Empty(_db.NextActions);
    }

    [Fact]
    public async Task Webhook_Client_IsLoggedOnTheClient()
    {
        var client = SeedClient();
        Assert.Equal(200, Status(await Post(Options(), ClickBody(client.Id, "client"))));
        Assert.Single(_db.Interactions.Where(i => i.ClientId == client.Id && i.LeadId == null));
    }

    // ---- Idempotency and threshold ---------------------------------------------------

    [Fact]
    public async Task Webhook_SameTokenAndTs_IsLoggedOnce()
    {
        var lead = SeedLead();
        var body = ClickBody(lead.Id);

        await Post(Options(), body);
        await Post(Options(), body);
        await Post(Options(), body);
        Assert.Single(_db.Interactions);

        // Another click on the same link (later ts) is a new interaction.
        await Post(Options(), ClickBody(lead.Id, ts: 1_800_000_060_000));
        Assert.Equal(2, _db.Interactions.Count());
    }

    [Fact]
    public async Task Webhook_BelowThreshold_PlansNoAction()
    {
        var lead = SeedLead();
        await Post(Options(threshold: 2), ClickBody(lead.Id, total: 1));
        Assert.Empty(_db.NextActions);
    }

    [Fact]
    public async Task Webhook_AtThreshold_PlansExactlyOneBelNu_AssignedToOwner_DueToday()
    {
        var lead = SeedLead();

        await Post(Options(threshold: 2), ClickBody(lead.Id, total: 2, ts: 1_800_000_000_000, token: "a"));
        await Post(Options(threshold: 2), ClickBody(lead.Id, total: 3, ts: 1_800_000_010_000, token: "a"));
        await Post(Options(threshold: 2), ClickBody(lead.Id, total: 4, ts: 1_800_000_020_000, token: "b"));
        // Replays of the first delivery must not add one either.
        await Post(Options(threshold: 2), ClickBody(lead.Id, total: 2, ts: 1_800_000_000_000, token: "a"));

        var action = Assert.Single(_db.NextActions);
        Assert.Equal("Bel nu: Hogenkamp las je bericht", action.Title);
        Assert.Equal(Me, action.AssignedToUserId);
        Assert.Equal(lead.Id, action.LeadId);
        Assert.Equal(3, _db.Interactions.Count());

        // It is on the Vandaag list, and not yet overdue.
        var today = (OkObjectResult)await new NextActionsController(_db, _access)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Me) }, "Test")),
                },
            },
        }.Today();
        var row = Assert.Single((IEnumerable<TodayActionDto>)today.Value!);
        Assert.Equal("Bel nu: Hogenkamp las je bericht", row.Title);
        Assert.False(row.Overdue);
    }

    [Fact]
    public async Task Webhook_AfterTheActionIsDone_AnotherHotClickPlansANewOne()
    {
        var lead = SeedLead();
        await Post(Options(), ClickBody(lead.Id, total: 2, ts: 1_800_000_000_000));
        _db.NextActions.Single().DoneAt = DateTime.UtcNow;
        _db.SaveChanges();

        await Post(Options(), ClickBody(lead.Id, total: 3, ts: 1_800_000_010_000));
        Assert.Equal(2, _db.NextActions.Count());
        Assert.Single(_db.NextActions.Where(a => a.DoneAt == null));
    }

    // ---- Proxy and link helper -------------------------------------------------------

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return Respond(request);
        }
    }

    private TrackController TrackApi(TrackOptions options, FakeHandler handler, string user = Me)
    {
        var gateway = new TrackGateway(Options(options), new HttpClient(handler));
        return new TrackController(_db, _access, gateway)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user) }, "Test")),
                },
            },
        };
    }

    private static HttpResponseMessage Json(object o) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };

    [Fact]
    public async Task Proxy_ReturnsHumanAndMachineClicks()
    {
        var lead = SeedLead();
        var handler = new FakeHandler
        {
            Respond = r => Json(new
            {
                entity_type = "lead",
                events = new object[]
                {
                    new { kind = "click", ts = 2L, is_human = true, url = "https://example.com/a", label = "A", campaign = "c", channel = "mail", device = "mobile" },
                    new { kind = "click", ts = 1L, is_human = false, bot_reason = "scanner", url = "https://example.com/a" },
                    new { kind = "site:pageview", ts = 3L },
                },
            }),
        };

        var ok = Assert.IsType<OkObjectResult>(await TrackApi(Options(), handler).Timeline("leads", lead.Id, CancellationToken.None));
        var dto = Assert.IsType<TrackTimelineDto>(ok.Value);
        Assert.True(dto.Enabled);
        Assert.True(dto.Available);
        Assert.Equal(1, dto.HumanClickCount);
        Assert.Equal("A", Assert.Single(dto.Human).Label);
        Assert.Equal("scanner", Assert.Single(dto.Machine).Reason);
        var call = Assert.Single(handler.Calls);
        Assert.EndsWith($"/api/entities/lead/{lead.Id}/timeline", call.Request.RequestUri!.AbsolutePath);
        Assert.Equal("unit-test-key", call.Request.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task Proxy_TrackUnreachable_ReturnsEmptyResult_NotAnError()
    {
        var lead = SeedLead();
        var handler = new FakeHandler { Respond = _ => throw new HttpRequestException("connection refused") };

        var ok = Assert.IsType<OkObjectResult>(await TrackApi(Options(), handler).Timeline("leads", lead.Id, CancellationToken.None));
        var dto = Assert.IsType<TrackTimelineDto>(ok.Value);
        Assert.True(dto.Enabled);
        Assert.False(dto.Available);
        Assert.Empty(dto.Human);
        Assert.Empty(dto.Machine);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        dto = (TrackTimelineDto)((OkObjectResult)await TrackApi(Options(), handler).Timeline("leads", lead.Id, CancellationToken.None)).Value!;
        Assert.False(dto.Available);
    }

    [Fact]
    public async Task Proxy_Disabled_ReturnsEmpty_AndNeverCallsTrack()
    {
        var lead = SeedLead();
        var handler = new FakeHandler();
        var ok = Assert.IsType<OkObjectResult>(await TrackApi(Options(enabled: false), handler).Timeline("leads", lead.Id, CancellationToken.None));
        Assert.False(((TrackTimelineDto)ok.Value!).Enabled);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Proxy_OtherUsersLead_IsNotFound()
    {
        var lead = SeedLead();
        var result = await TrackApi(Options(), new FakeHandler(), user: "someone-else").Timeline("leads", lead.Id, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Link_SendsEntityContext_AndReturnsShortUrl()
    {
        var client = SeedClient("ThuisNotaris");
        var handler = new FakeHandler { Respond = _ => Json(new { token = "abc", short_url = "https://go.example.com/t/abc", url = "https://example.com/x" }) };

        var ok = Assert.IsType<OkObjectResult>(await TrackApi(Options(), handler)
            .CreateLink("clients", client.Id, new CreateTrackedLinkDto("https://example.com/x", "najaar", "Offerte"), CancellationToken.None));

        Assert.Contains("https://go.example.com/t/abc", JsonSerializer.Serialize(ok.Value));
        using var sent = JsonDocument.Parse(Assert.Single(handler.Calls).Body);
        var root = sent.RootElement;
        Assert.Equal("client", root.GetProperty("entity_type").GetString());
        Assert.Equal(client.Id.ToString(), root.GetProperty("entity_id").GetString());
        Assert.Equal("ThuisNotaris", root.GetProperty("entity_label").GetString());
        Assert.Equal("leadmanager", root.GetProperty("source_tool").GetString());
        Assert.Equal("mail", root.GetProperty("channel").GetString());
        Assert.Equal("najaar", root.GetProperty("campaign").GetString());
        Assert.Equal("Offerte", root.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Link_Disabled_Is503_WithMessage()
    {
        var lead = SeedLead();
        var handler = new FakeHandler();
        var result = await TrackApi(Options(enabled: false), handler)
            .CreateLink("leads", lead.Id, new CreateTrackedLinkDto("https://example.com/x", null, null), CancellationToken.None);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, obj.StatusCode);
        Assert.Contains("niet aan", (string)obj.Value!);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Link_TrackRefuses_Is502_AndBadUrlIs400()
    {
        var lead = SeedLead();
        var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) };
        var api = TrackApi(Options(), handler);

        var refused = Assert.IsType<ObjectResult>(await api.CreateLink("leads", lead.Id, new CreateTrackedLinkDto("https://example.com/x", null, null), CancellationToken.None));
        Assert.Equal(502, refused.StatusCode);

        Assert.IsType<BadRequestObjectResult>(await api.CreateLink("leads", lead.Id, new CreateTrackedLinkDto("javascript:alert(1)", null, null), CancellationToken.None));
    }

    // ---- Real Node implementation ----------------------------------------------------

    /// <summary>
    /// Starts the real Jengo Track (Node) on a local port, registers a webhook that points at a listener in this
    /// test, clicks a tracked link as a human and feeds the webhook Track really sent into the receiver.
    /// Does nothing when the Track checkout or node is not available (CI); set TRACK_REPO to point elsewhere.
    /// </summary>
    [Fact]
    public async Task RealTrack_SignedWebhook_IsAccepted()
    {
        var repo = Environment.GetEnvironmentVariable("TRACK_REPO") ?? @"F:\projects\jengo-track";
        if (!File.Exists(Path.Combine(repo, "src", "server.js"))) return;

        const int trackPort = 5321;
        var adminKey = "lm-test-" + Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo("node", "--disable-warning=ExperimentalWarning src/server.js")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["TRACK_DB"] = ":memory:";
        psi.Environment["TRACK_PORT"] = trackPort.ToString();
        psi.Environment["TRACK_ADMIN_KEY"] = adminKey;
        psi.Environment["TRACK_SECRET"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        psi.Environment["TRACK_ALLOWED_DOMAINS"] = "example.com";

        Process? node;
        try { node = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return; }
        if (node == null) return;

        var listenerPort = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{listenerPort}/");
        listener.Start();
        var received = new List<(string Body, string? Signature)>();
        var pump = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                using var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                lock (received) received.Add((body, c.Request.Headers["X-Track-Signature"]));
                c.Response.StatusCode = 200;
                c.Response.Close();
            }
        });

        try
        {
            var baseUrl = $"http://127.0.0.1:{trackPort}";
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            http.DefaultRequestHeaders.Add("X-Api-Key", adminKey);

            // Wait until Track answers.
            var up = false;
            for (var i = 0; i < 50 && !up; i++)
            {
                try { up = (await http.GetAsync(baseUrl + "/api/webhooks")).IsSuccessStatusCode; }
                catch (HttpRequestException) { await Task.Delay(200); }
            }
            Assert.True(up, "Track did not start");

            var hook = await (await http.PostAsJsonAsync(baseUrl + "/api/webhooks",
                new { name = "leadmanager-test", url = $"http://127.0.0.1:{listenerPort}/hook", min_clicks = 1 })).Content.ReadFromJsonAsync<JsonElement>();
            var secret = hook.GetProperty("secret").GetString()!;

            var lead = SeedLead("Realtrack BV");
            var track = new JengoTrackClient(http, baseUrl, adminKey);
            var link = await track.CreateLinkAsync(new LinkRequest
            {
                Url = "https://example.com/offerte",
                EntityType = "lead",
                EntityId = lead.Id.ToString(),
                EntityLabel = "Realtrack BV",
                Campaign = "test",
                SourceTool = "leadmanager",
                Channel = "mail",
                Label = "Offerte",
            });
            Assert.NotNull(link);

            using var click = new HttpRequestMessage(HttpMethod.Get, link!.ShortUrl);
            click.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            click.Headers.AcceptLanguage.ParseAdd("nl-NL");
            var redirect = await http.SendAsync(click);
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);

            for (var i = 0; i < 50; i++)
            {
                lock (received) if (received.Count > 0) break;
                await Task.Delay(200);
            }
            (string Body, string? Signature) delivery;
            lock (received) delivery = Assert.Single(received);

            Assert.NotNull(delivery.Signature);
            var options = Options();
            options.WebhookSecret = secret;
            var result = await Webhook(options, delivery.Body, delivery.Signature).Receive(CancellationToken.None);
            Assert.Equal(200, Status(result));

            var interaction = Assert.Single(_db.Interactions.Where(i => i.LeadId == lead.Id));
            Assert.StartsWith("Klikte op Offerte", interaction.Summary);

            // And the same body with the wrong secret is refused.
            options.WebhookSecret = "not-the-secret-not-the-secret-123456";
            Assert.Equal(401, Status(await Webhook(options, delivery.Body, delivery.Signature).Receive(CancellationToken.None)));
        }
        finally
        {
            listener.Stop();
            try { node.Kill(entireProcessTree: true); } catch { /* already gone */ }
            node.Dispose();
        }
        await pump;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
