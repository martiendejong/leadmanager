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

/// <summary>JengoWork task 8090: the MCP tools for clients, projects, priority and the Acties overview.</summary>
public class McpClientToolsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;
    private readonly McpConnectorOptions _options = new() { ServerName = "jengo-clients", Realm = "JengoClients" };

    public McpClientToolsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _access = new CrmParentAccess(_db);
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private string SeedKey(string email)
    {
        var user = new ApplicationUser { UserName = email, Email = email, FirstName = "A", LastName = email, IsActive = true };
        _db.Users.Add(user);
        var key = ApiKeyDefaults.Generate();
        _db.ApiKeys.Add(new ApiKey { Name = "t", KeyHash = ApiKeyDefaults.Hash(key), Prefix = key[..8], UserId = user.Id });
        _db.SaveChanges();
        return key;
    }

    private async Task<(string text, bool isError)> Tool(string key, string tool, object? args = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = $"Bearer {key}";
        var controller = new LeadManagerMcpController(_options, new ApiKeyTokenValidator(_db), _db, _access, NullLogger<LeadManagerMcpController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        var body = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args ?? new { } } });
        var reply = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Post(body)).Value);
        var result = reply.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private static JsonElement Parse(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    [Fact]
    public async Task ToolList_HasTheNewClientTools()
    {
        var key = SeedKey("a@x.test");
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = $"Bearer {key}";
        var controller = new LeadManagerMcpController(_options, new ApiKeyTokenValidator(_db), _db, _access, NullLogger<LeadManagerMcpController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var body = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var reply = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Post(body)).Value);
        var names = reply.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        foreach (var n in new[] { "list_clients", "create_client", "set_client_priority", "create_project", "list_actions" })
            Assert.Contains(n, names);
    }

    [Fact]
    public async Task ClientLifecycle_CreateScoreProjectNoteAndList()
    {
        var key = SeedKey("a@x.test");

        var (created, err) = await Tool(key, "create_client", new { name = "ThuisNotaris", contact_name = "Bas", email = "bas@thuisnotaris.nl", engagement_category = "Active" });
        Assert.False(err, created);
        var clientId = Parse(created).GetProperty("id").GetGuid();

        var (dup, dupErr) = await Tool(key, "create_client", new { name = "thuisnotaris" });
        Assert.True(dupErr, dup);

        var (scored, scoreErr) = await Tool(key, "set_client_priority", new { client_id = clientId, value = 8, urgency = 5, effort = 2 });
        Assert.False(scoreErr, scored);
        Assert.Equal(10.0, Parse(scored).GetProperty("score").GetDouble());

        var (project, projErr) = await Tool(key, "create_project", new { client_id = clientId, name = "VvE-applicatie" });
        Assert.False(projErr, project);
        var projectId = Parse(project).GetProperty("id").GetGuid();

        var (logged, logErr) = await Tool(key, "log_interaction", new { parent_type = "clients", parent_id = clientId, kind = "Note", summary = "intake gehad", project_id = projectId });
        Assert.False(logErr, logged);
        Assert.Equal(projectId, Parse(logged).GetProperty("projectId").GetGuid());

        var (list, listErr) = await Tool(key, "list_clients", new { query = "thuis" });
        Assert.False(listErr, list);
        var row = Parse(list)[0];
        Assert.Equal("ThuisNotaris", row.GetProperty("name").GetString());
        Assert.Equal(10.0, row.GetProperty("score").GetDouble());
        Assert.Equal("VvE-applicatie", row.GetProperty("projects")[0].GetProperty("name").GetString());

        var (detail, detailErr) = await Tool(key, "get_lead", new { parent_type = "clients", parent_id = clientId });
        Assert.False(detailErr, detail);
        Assert.Equal(1, Parse(detail).GetProperty("details").GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task Tools_AreScopedToTheKeysUser()
    {
        var mine = SeedKey("mine@x.test");
        var theirs = SeedKey("theirs@x.test");
        var (created, _) = await Tool(theirs, "create_client", new { name = "Geheim BV" });
        var theirClient = Parse(created).GetProperty("id").GetGuid();

        var (list, _) = await Tool(mine, "list_clients");
        Assert.Equal(0, Parse(list).GetArrayLength());
        var (_, scoreErr) = await Tool(mine, "set_client_priority", new { client_id = theirClient, value = 1, urgency = 1, effort = 1 });
        Assert.True(scoreErr);
        var (_, projErr) = await Tool(mine, "create_project", new { client_id = theirClient, name = "x" });
        Assert.True(projErr);
    }

    [Fact]
    public async Task ListActions_ShowsOpenActionsAcrossParents()
    {
        var key = SeedKey("a@x.test");
        var (created, _) = await Tool(key, "create_client", new { name = "Acme" });
        var clientId = Parse(created).GetProperty("id").GetGuid();
        var (_, addErr) = await Tool(key, "add_next_action", new { parent_type = "clients", parent_id = clientId, title = "bel Bas", due_at = DateTime.UtcNow.AddDays(3).ToString("o") });
        Assert.False(addErr);

        var (list, listErr) = await Tool(key, "list_actions");
        Assert.False(listErr, list);
        Assert.Equal("bel Bas", Parse(list)[0].GetProperty("title").GetString());
        Assert.Equal("Acme", Parse(list)[0].GetProperty("parentName").GetString());
    }

    [Theory]
    [InlineData("\"8\"")]
    [InlineData("8.5")]
    [InlineData("true")]
    public async Task SetPriority_RefusesAValueThatIsNotAWholeNumber_InsteadOfClearing(string raw)
    {
        var key = SeedKey("a@x.test");
        var (created, _) = await Tool(key, "create_client", new { name = "Acme" });
        var clientId = Parse(created).GetProperty("id").GetGuid();
        await Tool(key, "set_client_priority", new { client_id = clientId, value = 8, urgency = 5, effort = 2 });

        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = $"Bearer {key}";
        var controller = new LeadManagerMcpController(_options, new ApiKeyTokenValidator(_db), _db, _access, NullLogger<LeadManagerMcpController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"set_client_priority\",\"arguments\":{\"client_id\":\"" + clientId + "\",\"value\":" + raw + "}}}";
        var reply = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Post(JsonSerializer.Deserialize<JsonElement>(json))).Value);
        Assert.True(reply.GetProperty("result").GetProperty("isError").GetBoolean());

        var stored = await _db.Clients.AsNoTracking().SingleAsync();
        Assert.Equal(8, stored.PriorityValue);
    }

    [Fact]
    public async Task LogInteraction_WithABadProjectId_IsAnError_NotASilentUnlink()
    {
        var key = SeedKey("a@x.test");
        var (created, _) = await Tool(key, "create_client", new { name = "Acme" });
        var clientId = Parse(created).GetProperty("id").GetGuid();
        var (text, isError) = await Tool(key, "log_interaction", new { parent_type = "clients", parent_id = clientId, kind = "Note", summary = "x", project_id = "geen-guid" });
        Assert.True(isError, text);
        Assert.Empty(await _db.Interactions.ToListAsync());
    }

    [Fact]
    public async Task Documents_AddChainAndList_ScopedToTheKeysUser()
    {
        var key = SeedKey("a@x.test");
        var other = SeedKey("b@x.test");
        var (created, _) = await Tool(key, "create_client", new { name = "ThuisNotaris" });
        var clientId = Parse(created).GetProperty("id").GetGuid();

        var (quote, qErr) = await Tool(key, "add_document", new { client_id = clientId, kind = "Quote", title = "Offerte intake", reference = "OFF-1", amount = 500 });
        Assert.False(qErr, quote);
        var quoteId = Parse(quote).GetProperty("id").GetGuid();
        var (dpa, dErr) = await Tool(key, "add_document", new { client_id = clientId, kind = "Dpa", title = "Verwerkersovereenkomst", status = "Signed", signed_at = "2026-11-12", predecessor_id = quoteId });
        Assert.False(dErr, dpa);
        Assert.NotNull(Parse(dpa).GetProperty("signedAt").GetString());

        var (list, lErr) = await Tool(key, "list_documents", new { client_id = clientId });
        Assert.False(lErr, list);
        Assert.Equal(2, Parse(list).GetArrayLength());

        var (_, foreignErr) = await Tool(other, "list_documents", new { client_id = clientId });
        Assert.True(foreignErr);
        var (_, foreignAdd) = await Tool(other, "add_document", new { client_id = clientId, kind = "Nda", title = "x" });
        Assert.True(foreignAdd);

        var (_, badAmount) = await Tool(key, "add_document", new { client_id = clientId, kind = "Other", title = "x", amount = "veel" });
        Assert.True(badAmount);
        var (_, badDate) = await Tool(key, "add_document", new { client_id = clientId, kind = "Other", title = "x", signed_at = "morgen" });
        Assert.True(badDate);
    }

    [Fact]
    public async Task A_key_cannot_start_a_lead_hunt_through_mcp_and_the_tool_is_not_listed()
    {
        var key = SeedKey("hunt@x.test");
        var (text, isError) = await Tool(key, "start_lead_hunt", new { segment = "graphic designers" });
        Assert.True(isError);
        Assert.Contains("not available with an API key", text);
        Assert.Empty(_db.HuntJobs);

        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = $"Bearer {key}";
        var controller = new LeadManagerMcpController(_options, new ApiKeyTokenValidator(_db), _db, _access, NullLogger<LeadManagerMcpController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var body = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var reply = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Post(body)).Value);
        var names = reply.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("start_lead_hunt", names);
        Assert.Contains("get_lead_hunt", names);
    }

    [Fact]
    public void No_mcp_tool_calls_an_action_that_is_closed_to_api_keys()
    {
        // Every MCP caller holds an API key, so the tool layer must never call a hunt action marked [DenyApiKey] (Start, ReviewByAgent, VerifyMany, VerifyOne, Cancel).
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LeadManager.Api", "Mcp", "LeadManagerMcpController.cs"));
        foreach (var call in new[] { "hunt.Start(", ".ReviewByAgent(", ".VerifyMany(", ".VerifyOne(", "hunt.Cancel(" })
            Assert.DoesNotContain(call, source);
    }
}
