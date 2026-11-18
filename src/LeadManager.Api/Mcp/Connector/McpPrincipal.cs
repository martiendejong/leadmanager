// Copied from jengo-components dotnet/mcp-connector (commit aac7103, 2026-09-25), unchanged except this header.
// Keep in sync: when a NuGet/project reference replaces the copy, delete this folder.
namespace JengoComponents.McpConnector;

/// <summary>
/// The authenticated caller of the /mcp endpoint, produced by an
/// <see cref="IMcpTokenValidator"/>. What it wraps depends on the token backend:
/// an API-key row (taskmanager tm_, jengo-knowledge km_) or the claims of an
/// HMAC bearer (jengo-live-meeting, jengo-agi, visit-platform style).
/// </summary>
public class McpPrincipal
{
    /// <summary>Stable subject identifier (user id, key id, IAM sub, ...).</summary>
    public string Subject { get; init; } = "";

    public string? Name { get; init; }

    public string? Email { get; init; }

    /// <summary>The raw bearer token that authenticated this request.</summary>
    public string RawToken { get; init; } = "";

    /// <summary>Backend-specific payload: the ApiKey entity, the parsed HMAC claims,
    /// or whatever the validator wants to hand to the tool layer.</summary>
    public object? Attachment { get; init; }
}
