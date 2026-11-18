using System.Text.Json;
using JengoComponents.McpConnector;
using LeadManager.Api.Data;
using LeadManager.Api.Mcp;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>JengoWork task 8030: the /mcp endpoint. Tools wrap the REST controllers as the user behind the API key.</summary>
public class McpServerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;
    private readonly McpConnectorOptions _options = new() { ServerName = "jengo-clients", Realm = "JengoClients" };

    public McpServerTests()
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

    // ---- plumbing ------------------------------------------------------------------------------------

    private (string userId, string key) SeedUserWithKey(string email, bool active = true, bool revoked = false)
    {
        var user = new ApplicationUser { UserName = email, Email = email, FirstName = "Agent", LastName = email, IsActive = active };
        _db.Users.Add(user);
        var key = ApiKeyDefaults.Generate();
        _db.ApiKeys.Add(new ApiKey
        {
            Name = "mcp test", KeyHash = ApiKeyDefaults.Hash(key), Prefix = key[..8], UserId = user.Id,
            RevokedAt = revoked ? DateTime.UtcNow : null,
        });
        _db.SaveChanges();
        return (user.Id, key);
    }

    private LeadManagerMcpController Controller(string? bearer)
    {
        var http = new DefaultHttpContext();
        if (bearer != null) http.Request.Headers.Authorization = $"Bearer {bearer}";
        http.Request.Host = new HostString("clients-test.example");
        return new LeadManagerMcpController(_options, new ApiKeyTokenValidator(_db), _db, _access,
            NullLogger<LeadManagerMcpController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static JsonElement Body(string method, object? args = null, object? id = null) =>
        JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = id ?? 1, method, @params = args });

    private static JsonElement Reply(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    /// <summary>Calls a tool and returns (text, isError).</summary>
    private async Task<(string text, bool isError)> Tool(string key, string tool, object? args = null)
    {
        var reply = Reply(await Controller(key).Post(Body("tools/call", new { name = tool, arguments = args ?? new { } })));
        var result = reply.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private static JsonElement Parse(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    private async Task<Guid> CreateLead(string key, string name = "Hogenkamp", string website = "https://www.hogenkamp.nl")
    {
        var (text, isError) = await Tool(key, "create_lead", new { name, website, phone = "088 244 25 00" });
        Assert.False(isError, text);
        return Parse(text).GetProperty("id").GetGuid();
    }

    // ---- authentication -----------------------------------------------------------------------------

    [Fact]
    public async Task NoToken_Is401_WithAWwwAuthenticateHeader()
    {
        var controller = Controller(null);

        var result = await controller.Post(Body("initialize"));

        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Contains("Bearer", controller.Response.Headers.WWWAuthenticate.ToString());
        Assert.Contains("JengoClients", controller.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task BadTokens_AreRefused()
    {
        var (_, revoked) = SeedUserWithKey("revoked@example.test", revoked: true);
        var (_, inactive) = SeedUserWithKey("inactive@example.test", active: false);

        foreach (var token in new[] { "lm_does-not-exist", "not-a-key", revoked, inactive })
            Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(await Controller(token).Post(Body("initialize"))).StatusCode);
    }

    [Fact]
    public async Task ValidToken_Initializes_AndEchoesTheProtocolVersion()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");

        var reply = Reply(await Controller(key).Post(Body("initialize", new { protocolVersion = "2025-06-18" })));

        Assert.Equal("jengo-clients", reply.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("2025-06-18", reply.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Notifications_AreAcknowledgedWithoutABody()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");
        var notification = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", method = "notifications/initialized" });

        Assert.Equal(202, Assert.IsType<StatusCodeResult>(await Controller(key).Post(notification)).StatusCode);
    }

    [Fact]
    public async Task UnknownMethod_IsAJsonRpcError_NotAnException()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");

        var reply = Reply(await Controller(key).Post(Body("resources/list")));

        Assert.Equal(-32601, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ---- tool catalogue -----------------------------------------------------------------------------

    [Fact]
    public async Task ToolList_HasEveryPlannedTool_WithConsistentSchemas()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");

        var tools = Reply(await Controller(key).Post(Body("tools/list"))).GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()).ToList();

        foreach (var expected in new[] { "search_leads", "find_by_email_or_domain", "get_lead", "create_lead", "log_interaction",
                                         "add_next_action", "complete_next_action", "next_actions_today", "add_contact", "link_item" })
            Assert.Contains(expected, names);
        Assert.Equal(names.Count, names.Distinct().Count());

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
            var schema = tool.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            // Every required argument must be described, or a client cannot know what to send.
            foreach (var required in schema.GetProperty("required").EnumerateArray())
                Assert.True(schema.GetProperty("properties").TryGetProperty(required.GetString()!, out _),
                    $"{tool.GetProperty("name")}: required '{required}' is not in properties");
        }
    }

    [Fact]
    public async Task LinkItemSchema_ListsExactlyTheLinkKinds()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");

        var tools = Reply(await Controller(key).Post(Body("tools/list"))).GetProperty("result").GetProperty("tools").EnumerateArray();
        var kinds = tools.Single(t => t.GetProperty("name").GetString() == "link_item")
            .GetProperty("inputSchema").GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(Enum.GetNames<EntityLinkKind>().OrderBy(x => x), kinds.OrderBy(x => x));
    }

    // ---- the working flow ---------------------------------------------------------------------------

    [Fact]
    public async Task ColdCallFlow_EndToEnd()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");
        var leadId = await CreateLead(key);

        var contact = await Tool(key, "add_contact", new { parent_id = leadId, name = "Werner Hogenkamp", email = "werner@hogenkamp.nl", mobile = "06 52 373 595" });
        Assert.False(contact.isError, contact.text);

        var call = await Tool(key, "log_interaction", new { parent_id = leadId, kind = "Call", summary = "Werner niet bereikbaar", outcome = "geen gehoor" });
        Assert.False(call.isError, call.text);
        Assert.Equal(PipelineStatus.Contacted, _db.Leads.Single().PipelineStatus);

        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("o");
        var action = await Tool(key, "add_next_action", new { parent_id = leadId, title = "Terugbellen na 15:00", due_at = yesterday });
        Assert.False(action.isError, action.text);
        var actionId = Parse(action.text).GetProperty("id").GetGuid();

        var link = await Tool(key, "link_item", new { parent_id = leadId, kind = "JengoWorkBoard", label = "Bord Hogenkamp", url = "https://tasks.prospergenics.com/boards/20" });
        Assert.False(link.isError, link.text);

        var today = Parse((await Tool(key, "next_actions_today")).text).EnumerateArray().ToList();
        var row = Assert.Single(today);
        Assert.Equal("Hogenkamp", row.GetProperty("parentName").GetString());
        Assert.True(row.GetProperty("overdue").GetBoolean());
        Assert.Equal("088 244 25 00", row.GetProperty("phone").GetString());

        var lead = Parse((await Tool(key, "get_lead", new { parent_id = leadId })).text);
        Assert.Equal("Hogenkamp", lead.GetProperty("details").GetProperty("name").GetString());
        Assert.Equal("Contacted", lead.GetProperty("details").GetProperty("pipeline").GetString());
        Assert.Single(lead.GetProperty("contacts").EnumerateArray());
        Assert.Single(lead.GetProperty("open_next_actions").EnumerateArray());
        Assert.Single(lead.GetProperty("links").EnumerateArray());
        Assert.Equal("Call", lead.GetProperty("latest_history")[0].GetProperty("kind").GetString());

        var done = await Tool(key, "complete_next_action", new { id = actionId });
        Assert.False(done.isError, done.text);
        Assert.Empty(Parse((await Tool(key, "next_actions_today")).text).EnumerateArray());
    }

    [Fact]
    public async Task CreateLead_IsIdempotent_AndRefusesUnsafeWebsites()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");
        var first = await CreateLead(key);
        var again = await Tool(key, "create_lead", new { name = "Hogenkamp Advocaten", website = "hogenkamp.nl" });

        Assert.Equal(first, Parse(again.text).GetProperty("id").GetGuid());
        Assert.Equal("matched", Parse(again.text).GetProperty("action").GetString());
        Assert.Single(_db.Leads);

        var evil = await Tool(key, "create_lead", new { name = "Evil", website = "javascript:alert(1)" });
        Assert.True(evil.isError);
        Assert.Contains("http", evil.text);
        Assert.Single(_db.Leads);
    }

    [Fact]
    public async Task LogInteraction_SameSourceRefTwice_IsLoggedOnce()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");
        var leadId = await CreateLead(key);
        var args = new { parent_id = leadId, kind = "Email", direction = "Outbound", summary = "Offerte", source_ref = "mail:3:Sent:409" };

        await Tool(key, "log_interaction", args);
        var second = await Tool(key, "log_interaction", args);

        Assert.False(second.isError);
        Assert.Single(_db.Interactions);
    }

    [Fact]
    public async Task Dates_WithoutAZone_AreStoredAsUtc()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");
        var leadId = await CreateLead(key);

        await Tool(key, "add_next_action", new { parent_id = leadId, title = "Bellen", due_at = "2026-11-19T09:00:00" });

        var due = _db.NextActions.Single().DueAt;
        Assert.Equal(new DateTime(2026, 11, 19, 9, 0, 0), new DateTime(due.Year, due.Month, due.Day, due.Hour, due.Minute, due.Second));
    }

    // ---- the same rules as REST -----------------------------------------------------------------------

    [Fact]
    public async Task AnotherUsersKey_CannotSee_OrChange_MyLead()
    {
        var (_, mine) = SeedUserWithKey("me@example.test");
        var (_, theirs) = SeedUserWithKey("them@example.test");
        var leadId = await CreateLead(mine);

        var get = await Tool(theirs, "get_lead", new { parent_id = leadId });
        var log = await Tool(theirs, "log_interaction", new { parent_id = leadId, kind = "Note", summary = "snoop" });
        var link = await Tool(theirs, "link_item", new { parent_id = leadId, kind = "Web", label = "x", url = "https://x.nl" });
        var search = Parse((await Tool(theirs, "search_leads", new { query = "hogenkamp" })).text);

        Assert.True(get.isError);
        Assert.True(log.isError);
        Assert.True(link.isError);
        Assert.Equal(0, search.GetArrayLength());
        Assert.Empty(_db.Interactions);
        Assert.Empty(_db.EntityLinks);
    }

    [Fact]
    public async Task Validation_Errors_ComeBackAsToolErrors_NotAsServerErrors()
    {
        var (_, key) = SeedUserWithKey("agent@example.test");
        var leadId = await CreateLead(key);

        Assert.True((await Tool(key, "log_interaction", new { parent_id = leadId, kind = "99", summary = "x" })).isError);
        Assert.True((await Tool(key, "log_interaction", new { parent_id = leadId, kind = "Call", summary = " " })).isError);
        Assert.True((await Tool(key, "add_next_action", new { parent_id = leadId, title = "x" })).isError);               // no due_at
        Assert.True((await Tool(key, "add_next_action", new { parent_id = leadId, title = "x", due_at = "tomorrow-ish" })).isError);
        Assert.True((await Tool(key, "link_item", new { parent_id = leadId, kind = "Web", label = "x", url = "javascript:alert(1)" })).isError);
        Assert.True((await Tool(key, "get_lead", new { parent_id = "not-a-guid" })).isError);
        Assert.True((await Tool(key, "get_lead", new { parent_id = leadId, parent_type = "projects" })).isError);
        Assert.True((await Tool(key, "no_such_tool")).isError);
        Assert.Empty(_db.Interactions);
        Assert.Empty(_db.NextActions);
    }

    [Fact]
    public async Task Clients_AreReachable_WithParentTypeClients()
    {
        var (userId, key) = SeedUserWithKey("agent@example.test");
        var client = new Client { Name = "ThuisNotaris", CreatedByUserId = userId, Website = "https://thuisnotaris.nl", PrimaryContactEmail = "bas@thuisnotaris.nl" };
        _db.Clients.Add(client);
        _db.SaveChanges();

        var log = await Tool(key, "log_interaction", new { parent_type = "clients", parent_id = client.Id, kind = "Meeting", summary = "Kick-off" });
        var get = Parse((await Tool(key, "get_lead", new { parent_type = "clients", parent_id = client.Id })).text);
        var find = Parse((await Tool(key, "find_by_email_or_domain", new { email = "bas@thuisnotaris.nl" })).text);

        Assert.False(log.isError, log.text);
        Assert.Equal("client", get.GetProperty("details").GetProperty("type").GetString());
        Assert.Equal("Meeting", get.GetProperty("latest_history")[0].GetProperty("kind").GetString());
        Assert.Equal("clients", find[0].GetProperty("parentType").GetString());
    }

    [Fact]
    public async Task TheKeyUser_IsLoggedOnTheCreatedRows()
    {
        var (userId, key) = SeedUserWithKey("agent@example.test");
        var leadId = await CreateLead(key);

        await Tool(key, "log_interaction", new { parent_id = leadId, kind = "Note", summary = "n" });

        Assert.Equal(userId, _db.Interactions.Single().CreatedByUserId);
        Assert.Equal(userId, _db.Leads.Single().ImportedByUserId);
    }
}
