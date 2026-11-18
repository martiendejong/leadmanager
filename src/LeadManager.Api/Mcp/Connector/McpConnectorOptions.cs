// Copied from jengo-components dotnet/mcp-connector (commit aac7103, 2026-09-25), unchanged except this header.
// Keep in sync: when a NuGet/project reference replaces the copy, delete this folder.
namespace JengoComponents.McpConnector;

/// <summary>
/// Settings for the MCP connector (OAuth 2.0 authorization server + /mcp endpoint).
/// Registered as a singleton by <see cref="ServiceCollectionExtensions.AddMcpConnector"/>.
/// </summary>
public class McpConnectorOptions
{
    /// <summary>serverInfo.name in the MCP initialize response, e.g. "jengo-knowledge".</summary>
    public string ServerName { get; set; } = "jengo-app";

    /// <summary>serverInfo.version in the MCP initialize response.</summary>
    public string ServerVersion { get; set; } = "1.0.0";

    /// <summary>Realm used in the WWW-Authenticate header on 401, e.g. "JengoKnowledge".</summary>
    public string Realm { get; set; } = "Jengo";

    /// <summary>Default MCP protocol version when the client does not send one on initialize.</summary>
    public string ProtocolVersion { get; set; } = "2025-03-26";

    /// <summary>OAuth scope advertised and issued. All reference apps use "mcp".</summary>
    public string Scope { get; set; } = "mcp";

    /// <summary>Prefix for dynamically registered client ids.</summary>
    public string ClientIdPrefix { get; set; } = "gpt_";

    /// <summary>Lifetime of an authorization code. All reference apps use 10 minutes.</summary>
    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Extra hosts (exact or as suffix match on subdomains) that may receive an
    /// authorization code, on top of the built-in chatgpt.com / chat.openai.com /
    /// claude.ai allowlist. https is always required. Auth codes carry a live access
    /// token, so this allowlist is the security boundary and it is enforced
    /// everywhere: registration rejects off-allowlist redirect_uris and authorize
    /// validation never accepts one, registered or not. Keep this list as small as
    /// possible.
    /// </summary>
    public IList<string> ExtraAllowedRedirectHosts { get; } = [];

    /// <summary>
    /// Cap on the in-memory client-registration store. Dynamic registration is
    /// unauthenticated per RFC 7591, so the store must be bounded; past the cap the
    /// oldest registration is evicted first. Evicted legitimate clients are
    /// transparently re-admitted at authorize time via the redirect allowlist.
    /// Rate-limit POST /oauth/register at your proxy as well.
    /// </summary>
    public int MaxRegisteredClients { get; set; } = 1000;
}
