using System.Text.Json;

namespace LeadManager.Api.Mcp;

/// <summary>
/// Protects /mcp before the shared connector sees the request (review of task 8030). The connector copy is
/// kept unchanged, so these limits live here:
///
/// - the body is capped (1 MB) and a batch may hold at most 20 calls: one authenticated key must not be able
///   to send tens of thousands of writes in a single request;
/// - malformed JSON-RPC ("jsonrpc": 2, a numeric "method", a tools/call without a name) is answered with a
///   proper JSON-RPC error instead of reaching code that would throw an exception message at the caller.
///
/// Only requests with a Bearer token are inspected; without one the connector answers 401 as usual.
/// </summary>
public sealed class McpRequestGuard
{
    public const int MaxBodyBytes = 1_000_000;
    public const int MaxBatch = 20;
    public const int MaxToolNameLength = 64;

    private readonly RequestDelegate _next;

    public McpRequestGuard(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/mcp")
            || !HttpMethods.IsPost(request.Method)
            || !request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            await Reject(context, StatusCodes.Status413PayloadTooLarge, -32600, "Request body is too large.");
            return;
        }

        // Read at most MaxBodyBytes + 1: a chunked body without Content-Length must be capped as well.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                await Reject(context, StatusCodes.Status413PayloadTooLarge, -32600, "Request body is too large.");
                return;
            }
        }

        var bytes = buffer.ToArray();
        string? problem;
        int code;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            (code, problem) = Validate(doc.RootElement);
        }
        catch (JsonException)
        {
            (code, problem) = (-32700, "Parse error.");
        }

        if (problem != null)
        {
            await Reject(context, StatusCodes.Status400BadRequest, code, problem);
            return;
        }

        request.Body = new MemoryStream(bytes); // hand the same body on to the connector
        await _next(context);
    }

    private static (int code, string? problem) Validate(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Array)
        {
            if (body.GetArrayLength() > MaxBatch)
                return (-32600, $"A batch may contain at most {MaxBatch} requests.");
            foreach (var item in body.EnumerateArray())
            {
                var (code, problem) = ValidateOne(item);
                if (problem != null) return (code, problem);
            }
            return (0, null);
        }
        return ValidateOne(body);
    }

    private static (int code, string? problem) ValidateOne(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object) return (-32600, "Invalid Request.");

        if (!msg.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            return (-32600, "Invalid Request: jsonrpc must be the string \"2.0\".");

        string? method = null;
        if (msg.TryGetProperty("method", out var m))
        {
            if (m.ValueKind != JsonValueKind.String) return (-32600, "Invalid Request: method must be a string.");
            method = m.GetString();
        }

        if (msg.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return (-32600, "Invalid Request: id must be a string, a number or null.");

        if (method == "tools/call")
        {
            if (!msg.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
                return (-32602, "Invalid params: params must be an object.");
            if (!p.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > MaxToolNameLength)
                return (-32602, "Invalid params: name must be a tool name.");
            if (p.TryGetProperty("arguments", out var args) && args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return (-32602, "Invalid params: arguments must be an object.");
        }

        return (0, null);
    }

    private static Task Reject(HttpContext context, int status, int code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = (object?)null, error = new { code, message } });
    }
}
