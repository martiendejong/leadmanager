// Copied from jengo-components dotnet/mcp-connector (commit aac7103, 2026-09-25), changed only here: loopback http and platform.openai.com in IsAllowedRedirect, 201 on registration.
// Keep in sync: when a NuGet/project reference replaces the copy, delete this folder.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace JengoComponents.McpConnector;

/// <summary>
/// In-memory OAuth 2.0 state for the MCP endpoint: dynamically registered clients
/// (RFC 7591) and short-lived authorization codes. ChatGPT re-registers automatically
/// after an app restart and codes live only minutes, so in-memory is fine · the
/// access token issued at the end of the dance (an API key or an HMAC bearer,
/// depending on the token backend) is what survives restarts.
///
/// Extracted from the identical service in taskmanager and jengo-knowledge
/// (JengoWork task 4162); the same logic runs in Node (jengo-live-meeting) and
/// WordPress (visit-platform).
/// </summary>
public class OAuthService(McpConnectorOptions options)
{
    public record ClientRecord(string ClientId, IReadOnlyList<string> RedirectUris, string? Name);

    public record CodeRecord(
        string ClientId, string AccessToken, string RedirectUri,
        string? CodeChallenge, string? CodeChallengeMethod, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, ClientRecord> _clients = new();
    private readonly ConcurrentQueue<string> _clientOrder = new();
    private readonly ConcurrentDictionary<string, CodeRecord> _codes = new();

    /// <summary>Register a client. Off-allowlist redirect_uris are NEVER stored
    /// (defense in depth: a stored URI must not be able to widen the allowlist,
    /// see <see cref="ValidateClientAndRedirect"/>); the controller additionally
    /// rejects such registrations outright. The store is bounded
    /// (<see cref="McpConnectorOptions.MaxRegisteredClients"/>, oldest evicted
    /// first) because registration is unauthenticated per RFC 7591; evicted
    /// legit clients are re-admitted at authorize time via the allowlist.</summary>
    public (string clientId, string clientSecret) RegisterClient(string? name, IEnumerable<string> redirectUris)
    {
        var clientId = options.ClientIdPrefix + RandomHex(10);
        AddClient(new ClientRecord(clientId, redirectUris.Where(IsAllowedRedirect).ToList(), name));
        return (clientId, RandomBase64(32));
    }

    private void AddClient(ClientRecord record)
    {
        if (!_clients.TryAdd(record.ClientId, record)) return;
        _clientOrder.Enqueue(record.ClientId);
        while (_clients.Count > options.MaxRegisteredClients && _clientOrder.TryDequeue(out var oldest))
            _clients.TryRemove(oldest, out _);
    }

    public bool TryGetClient(string clientId, out ClientRecord? client) =>
        _clients.TryGetValue(clientId, out client);

    /// <summary>Re-admit a client_id we forgot on restart (registrations are in-memory
    /// while ChatGPT keeps its old client_id). Only ever called after the redirect_uri
    /// passed <see cref="IsAllowedRedirect"/> · that allowlist, not the in-memory
    /// registry, is the security boundary, because auth codes carry a live token.</summary>
    public void EnsureClient(string clientId, string redirectUri) =>
        AddClient(new ClientRecord(clientId, [redirectUri], "Recovered client"));

    /// <summary>Hosts an auth code may ever be redirected to: https-only, the built-in
    /// ChatGPT/Claude hosts plus <see cref="McpConnectorOptions.ExtraAllowedRedirectHosts"/>.</summary>
    public bool IsAllowedRedirect(string? uri)
    {
        if (uri == null || !Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        // RFC 8252 section 8.3: loopback redirects (any port) for native clients such as Claude Code.
        if (u.Scheme == Uri.UriSchemeHttp) return u.Host == "localhost" || u.Host == "127.0.0.1";
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        var host = u.Host;
        if (HostMatches(host, "chatgpt.com") || host == "chat.openai.com" || host == "platform.openai.com" || HostMatches(host, "claude.ai"))
            return true;
        return options.ExtraAllowedRedirectHosts.Any(allowed => HostMatches(host, allowed));
    }

    private static bool HostMatches(string host, string allowed) =>
        host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal);

    /// <summary>Validate an incoming client_id + redirect_uri pair; re-admits allowlisted
    /// clients that were lost to a restart. Shared by the authorize page and submit.
    /// The allowlist is the ONLY boundary: a registered redirect_uri never widens it
    /// (registration is unauthenticated, so a stored URI proves nothing · an auth code
    /// carries a live access token and must never leave the allowlisted hosts).</summary>
    public bool ValidateClientAndRedirect(string clientId, string redirectUri, out string clientName)
    {
        clientName = "ChatGPT";
        if (!IsAllowedRedirect(redirectUri)) return false;
        if (TryGetClient(clientId, out var client))
        {
            clientName = client!.Name ?? clientName;
            return true;
        }
        EnsureClient(clientId, redirectUri);
        return true;
    }

    public string CreateCode(string clientId, string accessToken, string redirectUri,
        string? codeChallenge, string? codeChallengeMethod)
    {
        var code = RandomHex(24);
        _codes[code] = new CodeRecord(clientId, accessToken, redirectUri,
            codeChallenge, codeChallengeMethod, DateTime.UtcNow.Add(options.CodeLifetime));
        return code;
    }

    /// <summary>Single use: the code is removed on first consumption, expired codes return null.</summary>
    public CodeRecord? ConsumeCode(string code) =>
        _codes.TryRemove(code, out var rec) && rec.ExpiresAt > DateTime.UtcNow ? rec : null;

    /// <summary>RFC 7636 PKCE check. Only S256 is supported (also the only method advertised).
    /// Constant-time comparison so the challenge cannot be probed byte by byte.</summary>
    public static bool VerifyPkce(string verifier, string challenge, string method)
    {
        if (!method.Equals("S256", StringComparison.OrdinalIgnoreCase)) return false;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var computed = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.UTF8.GetBytes(challenge));
    }

    private static string RandomHex(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static string RandomBase64(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
}
