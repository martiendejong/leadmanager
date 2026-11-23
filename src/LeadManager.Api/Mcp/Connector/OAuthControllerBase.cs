// Copied from jengo-components dotnet/mcp-connector (commit aac7103, 2026-09-25), unchanged except this header.
// Keep in sync: when a NuGet/project reference replaces the copy, delete this folder.
using Microsoft.AspNetCore.Mvc;

namespace JengoComponents.McpConnector;

/// <summary>
/// Minimal OAuth 2.0 Authorization Server for an MCP endpoint: the authorization_code
/// + PKCE flow ChatGPT's and Claude's connectors require. Derive one controller from
/// this class and implement the two consent hooks; discovery, dynamic client
/// registration (RFC 7591) and the token endpoint are handled here.
///
/// Discovery chain the clients follow:
///   GET/POST /mcp (no token)                     · 401 + WWW-Authenticate (McpControllerBase)
///   GET /.well-known/oauth-protected-resource    · points at this AS
///   GET /.well-known/oauth-authorization-server  · AS metadata
///   POST /oauth/register                         · dynamic client registration
///   GET  /oauth/authorize                        · your consent page (or login bounce)
///   POST /oauth/token                            · code + PKCE verifier for access_token
///
/// Both well-known documents are served on THREE paths each: the bare path, the
/// RFC 9728 path-suffix form (/.well-known/...-resource/mcp) and the path-insertion
/// form (/mcp/.well-known/...) that some clients probe. If your app has an SPA
/// catch-all or a service worker, exempt /oauth/, /mcp and /.well-known/ from it,
/// otherwise the PWA shell answers these with index.html and a misleading 200.
///
/// The consent POST is yours to implement because it differs per app (server-rendered
/// form with cookies, client-side JS with a localStorage bearer, admin key mint vs
/// paste-a-key). In it, IN THIS ORDER: verify the caller is authenticated and the
/// antiforgery token is valid (ASP.NET's [ValidateAntiForgeryToken], or a nonce like
/// the Node/WP siblings) BEFORE any redirect, the deny path included; then validate
/// with <see cref="ValidateClientAndRedirect"/>, obtain an access token from your
/// token backend, and finish with <see cref="CompleteAuthorization"/> (or
/// <see cref="DenyRedirect"/>). A consent POST without CSRF protection lets any
/// website submit consent for a logged-in user.
/// </summary>
[ApiController]
public abstract class OAuthControllerBase(McpConnectorOptions options, OAuthService oauth) : ControllerBase
{
    protected McpConnectorOptions Options => options;
    protected OAuthService OAuth => oauth;

    // ── Consent hooks ────────────────────────────────────────────────────────────

    /// <summary>
    /// Render the consent page for an already-validated authorize request, or bounce
    /// an anonymous user through your login. When bouncing, the returnUrl MUST carry
    /// the FULL authorize URL including its query string
    /// (Request.Path + Request.QueryString); losing the query loses the PKCE
    /// challenge and state and the retry after login dead-ends.
    /// </summary>
    protected abstract Task<IActionResult> RenderConsentAsync(ConsentContext consent);

    // ── Discovery ────────────────────────────────────────────────────────────────

    [HttpGet("/.well-known/oauth-authorization-server")]
    [HttpGet("/.well-known/oauth-authorization-server/mcp")]
    [HttpGet("/mcp/.well-known/oauth-authorization-server")]
    public IActionResult AuthorizationServerMetadata()
    {
        var o = Request.GetPublicOrigin();
        return Ok(new
        {
            issuer = o,
            authorization_endpoint = $"{o}/oauth/authorize",
            token_endpoint = $"{o}/oauth/token",
            registration_endpoint = $"{o}/oauth/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none", "client_secret_post" },
            scopes_supported = new[] { options.Scope },
        });
    }

    [HttpGet("/.well-known/oauth-protected-resource")]
    [HttpGet("/.well-known/oauth-protected-resource/mcp")]
    [HttpGet("/mcp/.well-known/oauth-protected-resource")]
    public IActionResult ProtectedResourceMetadata()
    {
        var o = Request.GetPublicOrigin();
        return Ok(new
        {
            resource = $"{o}/mcp",
            authorization_servers = new[] { o },
            bearer_methods_supported = new[] { "header" },
        });
    }

    // ── Dynamic client registration (RFC 7591) ───────────────────────────────────

    /// <summary>Registration is unauthenticated per RFC 7591, so redirect_uris are
    /// validated against the https allowlist RIGHT HERE: an attacker must never be
    /// able to park an off-allowlist redirect_uri in the registry (auth codes carry
    /// a live access token). <see cref="OAuthService.RegisterClient"/> filters again
    /// as defense in depth.</summary>
    [HttpPost("/oauth/register")]
    public IActionResult Register([FromBody] ClientRegistrationRequest req)
    {
        var redirectUris = req.RedirectUris ?? [];
        if (redirectUris.Any(u => !oauth.IsAllowedRedirect(u)))
            return BadRequest(new
            {
                error = "invalid_redirect_uri",
                error_description = "Every redirect_uri must be https and on the redirect allowlist "
                    + "(chatgpt.com / chat.openai.com / claude.ai plus configured extra hosts).",
            });
        var (clientId, _) = oauth.RegisterClient(req.ClientName, redirectUris);
        return Ok(new
        {
            client_id = clientId,
            redirect_uris = redirectUris,
            client_name = req.ClientName ?? "MCP client",
            grant_types = new[] { "authorization_code" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        });
    }

    // ── Authorization endpoint (GET; the consent POST is app-specific) ───────────

    [HttpGet("/oauth/authorize")]
    public async Task<IActionResult> AuthorizePage(
        [FromQuery(Name = "client_id")] string? clientId,
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery] string? state,
        [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod)
    {
        if (clientId == null || redirectUri == null)
            return BadRequest("client_id and redirect_uri are required.");
        if (!oauth.ValidateClientAndRedirect(clientId, redirectUri, out var clientName))
            return BadRequest("Unknown client or redirect_uri not allowed.");

        return await RenderConsentAsync(new ConsentContext(
            clientId, clientName, redirectUri, state ?? "", codeChallenge, codeChallengeMethod));
    }

    // ── Token endpoint ────────────────────────────────────────────────────────────

    [HttpPost("/oauth/token")]
    [Consumes("application/x-www-form-urlencoded")]
    public IActionResult Token([FromForm] TokenRequest req)
    {
        if (!string.Equals(req.GrantType, "authorization_code", StringComparison.Ordinal))
            return BadRequest(new { error = "unsupported_grant_type" });

        var rec = oauth.ConsumeCode(req.Code ?? "");
        if (rec == null)
            return BadRequest(new { error = "invalid_grant", error_description = "Code expired or unknown." });

        // RFC 6749 §4.1.3: the token request must identify the client and repeat the
        // redirect_uri; both are bound to the code when it is minted and must match.
        if (!string.Equals(req.ClientId, rec.ClientId, StringComparison.Ordinal)
            || !string.Equals(req.RedirectUri, rec.RedirectUri, StringComparison.Ordinal))
            return BadRequest(new { error = "invalid_grant", error_description = "client_id or redirect_uri does not match the authorization request." });

        if (rec.CodeChallenge != null)
        {
            if (req.CodeVerifier == null ||
                !OAuthService.VerifyPkce(req.CodeVerifier, rec.CodeChallenge, rec.CodeChallengeMethod ?? "S256"))
                return BadRequest(new { error = "invalid_grant", error_description = "PKCE verification failed." });
        }

        return Ok(new { access_token = rec.AccessToken, token_type = "Bearer", scope = options.Scope });
    }

    // ── Helpers for your consent POST ────────────────────────────────────────────

    /// <summary>Validate a client_id + redirect_uri pair coming back on the consent
    /// submit (re-admits allowlisted clients lost to a restart).</summary>
    protected bool ValidateClientAndRedirect(string clientId, string redirectUri) =>
        oauth.ValidateClientAndRedirect(clientId, redirectUri, out _);

    /// <summary>Mint the authorization code for an approved consent and build the
    /// redirect URL back to the client. accessToken is whatever your token backend
    /// produced (a freshly minted API key, or <see cref="HmacTokenService.Issue"/>).</summary>
    protected string CompleteAuthorization(string clientId, string redirectUri, string? state,
        string? codeChallenge, string? codeChallengeMethod, string accessToken)
    {
        var code = oauth.CreateCode(clientId, accessToken, redirectUri, codeChallenge, codeChallengeMethod);
        var sep = redirectUri.Contains('?') ? "&" : "?";
        return $"{redirectUri}{sep}code={code}&state={Uri.EscapeDataString(state ?? "")}";
    }

    /// <summary>Redirect URL for a denied consent. Only build this AFTER your consent
    /// POST has verified authentication and its antiforgery token · redirecting first
    /// turns an unauthenticated cross-site POST into an open redirect.</summary>
    protected static string DenyRedirect(string redirectUri, string? state)
    {
        var sep = redirectUri.Contains('?') ? "&" : "?";
        return $"{redirectUri}{sep}error=access_denied&state={Uri.EscapeDataString(state ?? "")}";
    }

    /// <summary>Login bounce that preserves the full authorize query (state + PKCE).</summary>
    protected IActionResult LoginChallenge(string loginPath = "/api/auth/login", string returnUrlParam = "returnUrl") =>
        Redirect($"{loginPath}?{returnUrlParam}=" + Uri.EscapeDataString(Request.Path + Request.QueryString));
}

/// <summary>An authorize request that passed client + redirect validation.</summary>
public record ConsentContext(
    string ClientId, string ClientName, string RedirectUri, string State,
    string? CodeChallenge, string? CodeChallengeMethod);

public class ClientRegistrationRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("client_name")]
    public string? ClientName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("redirect_uris")]
    public List<string>? RedirectUris { get; set; }
}

public class TokenRequest
{
    [Microsoft.AspNetCore.Mvc.FromForm(Name = "grant_type")] public string? GrantType { get; set; }
    [Microsoft.AspNetCore.Mvc.FromForm(Name = "code")] public string? Code { get; set; }
    [Microsoft.AspNetCore.Mvc.FromForm(Name = "code_verifier")] public string? CodeVerifier { get; set; }
    [Microsoft.AspNetCore.Mvc.FromForm(Name = "redirect_uri")] public string? RedirectUri { get; set; }
    [Microsoft.AspNetCore.Mvc.FromForm(Name = "client_id")] public string? ClientId { get; set; }
}
