// Copied from jengo-components dotnet/mcp-connector (commit aac7103, 2026-09-25), unchanged except this header.
// Keep in sync: when a NuGet/project reference replaces the copy, delete this folder.
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace JengoComponents.McpConnector;

/// <summary>
/// In-process MCP endpoint for ChatGPT/Claude. The JSON-RPC subset the connectors
/// need (initialize, notifications/initialized, tools/list, tools/call, ping) is
/// answered as plain application/json; the StreamableHTTP spec allows a plain JSON
/// reply for a one-shot response and both clients accept it, so there is no SSE.
///
/// IN-PROCESS is the point: running the MCP handler inside the same app instead of
/// reverse-proxying to a sidecar removes the whole class of proxy/keep-alive framing
/// bugs. The taskmanager proxy variant cost hours: forcing Connection: close on the
/// proxy-to-Node call made every SECOND request come back as an empty 400, so
/// ChatGPT's second POST (notifications/initialized) failed and the connector showed
/// no tools. If you cannot avoid a proxy, never force Connection: close, buffer both
/// bodies, and reply with an explicit Content-Length.
///
/// Derive one controller, pass your <see cref="IMcpTokenValidator"/> and implement
/// the two tool members. Both GET and POST answer 401 + WWW-Authenticate without a
/// valid token: some clients discover auth by GETting the endpoint first, and a 405
/// there would hide the OAuth pointer.
/// </summary>
[ApiController]
[Route("mcp")]
public abstract class McpControllerBase(McpConnectorOptions options, IMcpTokenValidator tokens) : ControllerBase
{
    /// <summary>The tools/list payload: objects (or JsonNodes) each shaped
    /// {name, description, inputSchema}.</summary>
    protected abstract IReadOnlyList<object> GetTools(McpPrincipal principal);

    /// <summary>Execute one tool call. Return (text, isError); throwing maps to a
    /// JSON-RPC -32603 error instead.</summary>
    protected abstract Task<(string text, bool isError)> CallToolAsync(
        string name, JsonElement args, McpPrincipal principal, CancellationToken ct);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if (await Authenticate() == null) return Unauthorized401();
        return StatusCode(405, new { error = "Method Not Allowed", hint = "POST a JSON-RPC MCP request." });
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonElement body)
    {
        var principal = await Authenticate();
        if (principal == null) return Unauthorized401();

        if (body.ValueKind == JsonValueKind.Array)
        {
            if (body.GetArrayLength() == 0)   // JSON-RPC 2.0: an empty batch is Invalid Request
                return Ok(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32600, message = "Invalid Request" } });
            var outArr = new List<object>();
            foreach (var el in body.EnumerateArray())
            {
                var r = await HandleRpc(el, principal);
                if (r != null) outArr.Add(r);
            }
            return Ok(outArr);
        }

        var result = await HandleRpc(body, principal);
        if (result == null) return StatusCode(202);   // notification: acknowledged, no body
        return Ok(result);
    }

    private async ValueTask<McpPrincipal?> Authenticate()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = auth["Bearer ".Length..].Trim();
        if (token.Length == 0) return null;
        return await tokens.ValidateAsync(token, HttpContext.RequestAborted);
    }

    private IActionResult Unauthorized401()
    {
        var origin = Request.GetPublicOrigin();
        Response.Headers.WWWAuthenticate =
            $"Bearer realm=\"{options.Realm}\", resource_metadata=\"{origin}/.well-known/oauth-protected-resource\"";
        return StatusCode(401, new { error = "unauthorized", error_description = "Bearer token required." });
    }

    // ── JSON-RPC dispatch ────────────────────────────────────────────────────────

    private async Task<object?> HandleRpc(JsonElement msg, McpPrincipal principal)
    {
        if (msg.ValueKind != JsonValueKind.Object
            || !msg.TryGetProperty("jsonrpc", out var v) || v.GetString() != "2.0")
            return new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32600, message = "Invalid Request" } };

        var hasId = msg.TryGetProperty("id", out var idEl);
        object? id = hasId ? JsonElementToObject(idEl) : null;
        var method = msg.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;

        if (!hasId || idEl.ValueKind == JsonValueKind.Null) return null;   // notification

        try
        {
            switch (method)
            {
                case "initialize":
                    var requested = msg.TryGetProperty("params", out var ip)
                        && ip.ValueKind == JsonValueKind.Object
                        && ip.TryGetProperty("protocolVersion", out var pv)
                        && pv.ValueKind == JsonValueKind.String
                            ? pv.GetString() : null;
                    return Rpc(id, new
                    {
                        protocolVersion = requested ?? options.ProtocolVersion,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = options.ServerName, version = options.ServerVersion },
                    });
                case "ping":
                    return Rpc(id, new { });
                case "tools/list":
                    return Rpc(id, new { tools = GetTools(principal) });
                case "tools/call":
                    {
                        var p = msg.GetProperty("params");
                        var name = p.GetProperty("name").GetString() ?? "";
                        var args = p.TryGetProperty("arguments", out var a) ? a : default;
                        var (text, isError) = await CallToolAsync(name, args, principal, HttpContext.RequestAborted);
                        return Rpc(id, new { content = new[] { new { type = "text", text } }, isError });
                    }
                default:
                    return new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Method not found: {method}" } };
            }
        }
        catch (Exception ex)
        {
            return new { jsonrpc = "2.0", id, error = new { code = -32603, message = ex.Message } };
        }
    }

    private static object Rpc(object? id, object result) => new { jsonrpc = "2.0", id, result };

    private static object? JsonElementToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        _ => e.ToString(),
    };

    // ── Argument helpers for tool implementations ────────────────────────────────

    protected static string? ArgStr(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    protected static bool? ArgBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    protected static int? ArgInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
