using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using JengoComponents.McpConnector;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Mcp;

/// <summary>
/// OAuth 2.0 authorization server for /mcp, so ChatGPT and Claude.ai can add Clients as a custom connector
/// (discovery, dynamic client registration, authorization code + PKCE S256 come from the shared component).
///
/// The SPA keeps its session as a JWT in localStorage ("lm_token"), not in a cookie, so consent is client-side:
/// the page reads that token, shows who is signed in, and on "Allow" posts it as a Bearer header to
/// /oauth/consent. The server then mints a personal lm_ API key and hands it out as the OAuth access_token, so
/// the connector acts exactly as that user (organisations, ownership, the [DenyApiKey] limits) and the key
/// shows up on the Integrations page where it can be revoked. A bearer header is not sent by a foreign
/// website, so the consent POST needs no antiforgery token.
/// </summary>
public class LeadManagerOAuthController : OAuthControllerBase
{
    /// <summary>Same names as before the connector: one key per client kind, rotated at every consent.</summary>
    public const string ChatGptKeyName = "ChatGPT connector";
    public const string ClaudeKeyName = "Claude connector";
    public const string OtherKeyName = "MCP connector";

    private readonly LeadManagerDbContext _db;

    public LeadManagerOAuthController(McpConnectorOptions options, OAuthService oauth, LeadManagerDbContext db)
        : base(options, oauth) => _db = db;

    /// <summary>The key name for a redirect host: the key list on Integrations then says which connector it is.</summary>
    public static string KeyNameFor(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var u)) return OtherKeyName;
        var h = u.Host;
        if (h == "chatgpt.com" || h.EndsWith(".chatgpt.com", StringComparison.Ordinal) || h == "chat.openai.com" || h == "platform.openai.com") return ChatGptKeyName;
        if (h == "claude.ai" || h.EndsWith(".claude.ai", StringComparison.Ordinal)
            || h == "claude.com" || h.EndsWith(".claude.com", StringComparison.Ordinal)) return ClaudeKeyName;
        return OtherKeyName;
    }

    protected override Task<IActionResult> RenderConsentAsync(ConsentContext c)
    {
        // The consent page reads the session token in the browser; nothing about the user is known here.
        // PKCE is mandatory: both ChatGPT and Claude send an S256 challenge, anything else is refused up front.
        if (string.IsNullOrEmpty(c.CodeChallenge) || !string.Equals(c.CodeChallengeMethod, "S256", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IActionResult>(BadRequest("code_challenge with code_challenge_method=S256 is required."));

        // JSON with the default encoder escapes <, > and & as \uXXXX, so the values cannot break out of the script.
        var json = JsonSerializer.Serialize(new
        {
            client_id = c.ClientId,
            client_name = c.ClientName,
            redirect_uri = c.RedirectUri,
            state = c.State,
            code_challenge = c.CodeChallenge,
            code_challenge_method = "S256",
        });
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        return Task.FromResult<IActionResult>(Content(ConsentHtml.Replace("__PARAMS__", json), "text/html; charset=utf-8"));
    }

    /// <summary>Allow: mint a personal lm_ key for the signed-in user (rotating the previous one of this connector
    /// kind) and answer with the redirect URL carrying the authorization code. JWT only: an API key can never
    /// authorize a connector, so a leaked key cannot mint further keys.</summary>
    [HttpPost("/oauth/consent")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> Consent([FromBody] ConsentSubmit req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.ClientId) || string.IsNullOrEmpty(req.RedirectUri))
            return BadRequest(new { error = "client_id and redirect_uri are required." });
        if (!ValidateClientAndRedirect(req.ClientId, req.RedirectUri))
            return BadRequest(new { error = "Unknown client or redirect_uri not allowed." });
        if (string.IsNullOrEmpty(req.CodeChallenge) || !string.Equals(req.CodeChallengeMethod, "S256", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "code_challenge with code_challenge_method=S256 is required." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userId)) return Unauthorized(new { error = "Sign in to Clients first." });
        var active = await _db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct);
        if (!active) return Unauthorized(new { error = "Sign in to Clients first." });

        var name = KeyNameFor(req.RedirectUri);
        var now = DateTime.UtcNow;
        var old = await _db.ApiKeys.Where(k => k.UserId == userId && k.Name == name && k.RevokedAt == null).ToListAsync(ct);
        foreach (var k in old) k.RevokedAt = now;

        var key = ApiKeyDefaults.Generate();
        _db.ApiKeys.Add(new ApiKey
        {
            Name = name,
            KeyHash = ApiKeyDefaults.Hash(key),
            Prefix = key[..Math.Min(key.Length, 8)],
            UserId = userId,
        });
        await _db.SaveChangesAsync(ct);

        var redirect = CompleteAuthorization(req.ClientId, req.RedirectUri, req.State, req.CodeChallenge, req.CodeChallengeMethod, key);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { redirect });
    }

    public class ConsentSubmit
    {
        [JsonPropertyName("client_id")] public string? ClientId { get; set; }
        [JsonPropertyName("redirect_uri")] public string? RedirectUri { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("code_challenge")] public string? CodeChallenge { get; set; }
        [JsonPropertyName("code_challenge_method")] public string? CodeChallengeMethod { get; set; }
    }

    // The page needs the same origin as the SPA (localStorage "lm_token"). Values are injected as JSON (see above)
    // and written into the DOM with textContent only.
    private const string ConsentHtml = """
<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<meta name="robots" content="noindex"/>
<title>Clients · Authorize</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#0f172a;color:#f1f5f9;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:1rem}
.card{background:#1e293b;border:1px solid #334155;border-radius:12px;padding:2rem;max-width:440px;width:100%;box-shadow:0 8px 32px #0008}
h1{font-size:1.25rem;margin-bottom:.5rem;color:#f8fafc}
.sub{color:#94a3b8;font-size:.9rem;margin-bottom:1rem;line-height:1.55}
.who{background:#0f172a;border:1px solid #334155;border-radius:8px;padding:.6rem .8rem;font-size:.85rem;color:#cbd5e1;margin-bottom:1rem}
.btn{display:block;width:100%;text-align:center;padding:.75rem;border:none;border-radius:8px;background:#6366f1;color:#fff;font-size:1rem;font-weight:600;cursor:pointer;text-decoration:none}
.btn:hover{background:#4f46e5}.btn:disabled{opacity:.5;cursor:default}
.cancel{display:block;text-align:center;margin-top:.75rem;color:#94a3b8;font-size:.9rem;cursor:pointer}
.cancel:hover{color:#f1f5f9}
.hint{color:#64748b;font-size:.8rem;margin-top:.9rem;line-height:1.45}
.err{background:#450a0a;border:1px solid #7f1d1d;color:#fca5a5;padding:.6rem .8rem;border-radius:8px;font-size:.85rem;margin-bottom:1rem}
ul{margin:.4rem 0 0 1.1rem;color:#94a3b8;font-size:.82rem;line-height:1.5}
</style></head>
<body><div class="card">
<h1 id="title">Give access to Clients?</h1>
<p class="sub" id="lead"></p>
<ul><li>read and search your leads and clients</li><li>log interactions, plan actions, add contacts and documents</li><li>draft messages (nothing is ever sent by the app)</li></ul>
<div id="body" style="margin-top:1rem"><p class="sub">Checking your session...</p></div>
<p class="hint">Access is tied to your account and to the organisations you belong to. Allowing creates a personal API key, listed under Integrations, which you can revoke at any time. The connector cannot delete anything.</p>
</div>
<script>
var P = __PARAMS__;
var token = null;
try { token = localStorage.getItem('lm_token'); } catch (e) {}
var body = document.getElementById('body');
document.getElementById('title').textContent = 'Give ' + P.client_name + ' access to Clients?';
document.getElementById('lead').textContent = P.client_name + ' wants to use your Clients (CRM) account through the MCP connector:';
function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function clear() { while (body.firstChild) body.removeChild(body.firstChild); }
function deny() {
  var sep = P.redirect_uri.indexOf('?') >= 0 ? '&' : '?';
  location.href = P.redirect_uri + sep + 'error=access_denied&state=' + encodeURIComponent(P.state || '');
}
function showSignIn() {
  clear();
  body.appendChild(el('div', 'err', 'You are not signed in to Clients in this browser.'));
  var a = el('a', 'btn', 'Sign in to Clients');
  a.href = '/login';
  a.onclick = function () {
    // The login page sends the user back here once signed in.
    try { localStorage.setItem('lm_oauth_return', JSON.stringify({ url: location.pathname + location.search, ts: Date.now() })); } catch (e) {}
  };
  body.appendChild(a);
  var c = el('a', 'cancel', 'Cancel'); c.onclick = deny; body.appendChild(c);
}
function showConsent(name) {
  clear();
  var who = el('div', 'who', 'Signed in as '); who.appendChild(el('strong', null, name)); body.appendChild(who);
  var allow = el('button', 'btn', 'Allow access'); body.appendChild(allow);
  var c = el('a', 'cancel', 'Cancel'); c.onclick = deny; body.appendChild(c);
  allow.onclick = function () {
    allow.disabled = true; allow.textContent = 'Authorizing...';
    fetch('/oauth/consent', {
      method: 'POST',
      headers: { 'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json' },
      body: JSON.stringify(P)
    }).then(function (r) {
      if (r.status === 401) { try { localStorage.removeItem('lm_token'); } catch (e) {} showSignIn(); return null; }
      return r.json().then(function (d) { if (!r.ok) throw new Error(d.error || 'Failed'); return d; });
    }).then(function (d) { if (d) location.href = d.redirect; })
      .catch(function (e) {
        allow.disabled = false; allow.textContent = 'Allow access';
        body.insertBefore(el('div', 'err', e.message), body.firstChild);
      });
  };
}
if (!token) { showSignIn(); }
else {
  fetch('/api/auth/me', { headers: { 'Authorization': 'Bearer ' + token } })
    .then(function (r) { return r.ok ? r.json() : Promise.reject(); })
    .then(function (me) { showConsent(((me.firstName || '') + ' ' + (me.lastName || '')).trim() || me.email || 'you'); })
    .catch(function () { showSignIn(); });
}
</script>
</body></html>
""";
}
