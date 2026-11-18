// Copied from jengo-components dotnet/mcp-connector (commit aac7103, 2026-09-25), unchanged except this header.
// Keep in sync: when a NuGet/project reference replaces the copy, delete this folder.
namespace JengoComponents.McpConnector;

/// <summary>
/// Pluggable token backend for the /mcp endpoint. Two shapes exist in production:
///
/// 1. API-key table: the OAuth access_token IS a real API key (taskmanager tm_,
///    jengo-knowledge km_, jengomail's scoped keys). Implement this interface by
///    looking the key up in your table (see <see cref="DelegatingTokenValidator"/>),
///    and mint the key yourself in your consent action. Revocable from the app's
///    own key UI, survives restarts and secret rotation.
///
/// 2. HMAC bearer: the app has no key table, so the token is an HMAC-signed
///    payload carrying the user identity (jengo-live-meeting, jengo-agi,
///    visit-platform). Use <see cref="HmacTokenService"/> for both issuing and
///    validating. No storage, but rotating the secret invalidates every token.
/// </summary>
public interface IMcpTokenValidator
{
    /// <summary>Validate a raw bearer token (already stripped of the "Bearer " prefix).
    /// Return null when invalid, expired or revoked.</summary>
    ValueTask<McpPrincipal?> ValidateAsync(string token, CancellationToken ct = default);
}

/// <summary>API-key-table backend: wraps the app's own key lookup as a delegate,
/// so the component has no opinion about your DbContext or hash scheme.</summary>
public class DelegatingTokenValidator(Func<string, CancellationToken, ValueTask<McpPrincipal?>> validate)
    : IMcpTokenValidator
{
    public ValueTask<McpPrincipal?> ValidateAsync(string token, CancellationToken ct = default) =>
        validate(token, ct);
}
