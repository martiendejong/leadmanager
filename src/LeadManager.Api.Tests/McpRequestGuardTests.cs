using System.Text;
using System.Text.Json;
using LeadManager.Api.Mcp;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>Review findings of task 8030: size and batch caps, malformed JSON-RPC, pass-through behaviour.</summary>
public class McpRequestGuardTests
{
    private sealed record Outcome(bool ReachedNext, int Status, string Response, string BodySeenByNext);

    private static async Task<Outcome> Run(string method, string path, string? body, bool bearer = true, long? contentLength = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (bearer) context.Request.Headers.Authorization = "Bearer lm_whatever";
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = contentLength == -1 ? null : contentLength ?? bytes.Length; // -1 = chunked, no length
        context.Response.Body = new MemoryStream();

        var reached = false;
        var seen = "";
        var guard = new McpRequestGuard(async ctx =>
        {
            reached = true;
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            seen = await reader.ReadToEndAsync();
        });
        await guard.InvokeAsync(context);

        context.Response.Body.Position = 0;
        return new Outcome(reached, context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync(), seen);
    }

    private static int ErrorCode(Outcome o) => JsonDocument.Parse(o.Response).RootElement.GetProperty("error").GetProperty("code").GetInt32();

    private const string Valid = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"next_actions_today\",\"arguments\":{}}}";

    [Fact]
    public async Task ValidRequest_PassesThrough_WithTheBodyIntact()
    {
        var o = await Run("POST", "/mcp", Valid);

        Assert.True(o.ReachedNext);
        Assert.Equal(Valid, o.BodySeenByNext);
    }

    [Theory]
    [InlineData("GET", "/mcp")]
    [InlineData("POST", "/api/leads")]
    [InlineData("DELETE", "/mcp")]
    public async Task OtherRequests_AreLeftAlone(string method, string path)
    {
        var o = await Run(method, path, "{not even json");

        Assert.True(o.ReachedNext);
    }

    [Fact]
    public async Task WithoutBearer_TheConnectorAnswers401_TheGuardDoesNotParse()
    {
        var o = await Run("POST", "/mcp", "{not even json", bearer: false);

        Assert.True(o.ReachedNext);
    }

    [Fact]
    public async Task BatchOfTwentyIsFine_TwentyOneIsRefused()
    {
        string Batch(int n) => "[" + string.Join(",", Enumerable.Repeat("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", n)) + "]";

        Assert.True((await Run("POST", "/mcp", Batch(McpRequestGuard.MaxBatch))).ReachedNext);

        var tooMany = await Run("POST", "/mcp", Batch(McpRequestGuard.MaxBatch + 1));
        Assert.False(tooMany.ReachedNext);
        Assert.Equal(400, tooMany.Status);
        Assert.Equal(-32600, ErrorCode(tooMany));
    }

    [Fact]
    public async Task OversizedBody_IsRefused_ByContentLength_AndWhenChunked()
    {
        var huge = new string('x', McpRequestGuard.MaxBodyBytes + 10);

        var byLength = await Run("POST", "/mcp", huge);
        Assert.False(byLength.ReachedNext);
        Assert.Equal(413, byLength.Status);

        // No Content-Length (chunked): the cap must still hold while reading.
        var chunked = await Run("POST", "/mcp", huge, contentLength: -1);
        Assert.Equal(413, chunked.Status);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":2,\"id\":1,\"method\":\"ping\"}")]                                  // jsonrpc not a string
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"ping\"}")]                            // wrong version
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":42}")]                                  // numeric method
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{\"a\":1},\"method\":\"ping\"}")]                    // object id
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    public async Task MalformedJsonRpc_GetsAnInvalidRequestError_NotAServerError(string body)
    {
        var o = await Run("POST", "/mcp", body);

        Assert.False(o.ReachedNext);
        Assert.Equal(400, o.Status);
        Assert.Equal(-32600, ErrorCode(o));
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}")]                                            // no params
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":[]}")]                              // params not an object
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{}}")]                              // no name
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":7}}")]                    // name not a string
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"a\",\"arguments\":[1]}}")] // arguments not an object
    public async Task MalformedToolCall_GetsInvalidParams(string body)
    {
        var o = await Run("POST", "/mcp", body);

        Assert.False(o.ReachedNext);
        Assert.Equal(-32602, ErrorCode(o));
    }

    [Fact]
    public async Task OverlongToolName_IsRefused()
    {
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + new string('a', McpRequestGuard.MaxToolNameLength + 1) + "\"}}";

        Assert.False((await Run("POST", "/mcp", body)).ReachedNext);
    }

    [Fact]
    public async Task InvalidJson_IsAParseError()
    {
        var o = await Run("POST", "/mcp", "{\"jsonrpc\":");

        Assert.False(o.ReachedNext);
        Assert.Equal(-32700, ErrorCode(o));
    }

    [Fact]
    public async Task Notifications_WithoutAnId_StillPass()
    {
        var o = await Run("POST", "/mcp", "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

        Assert.True(o.ReachedNext);
    }
}
