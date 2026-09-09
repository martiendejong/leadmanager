using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LeadManager.Api.Controllers;

[ApiController]
[Route("api/auth/sso")]
public class AuthSsoController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, string> _pkceStore = new();
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly JwtService _jwtService;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuthSsoController> _logger;

    private const string IamIssuer = "https://maendeleo.martiendejong.nl";
    private const string IamClientId = "leadmanager";

    public AuthSsoController(
        UserManager<ApplicationUser> userManager,
        JwtService jwtService,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthSsoController> logger)
    {
        _userManager = userManager;
        _jwtService = jwtService;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // GET /api/auth/sso/start  → returns { authorizeUrl }
    [HttpGet("start")]
    [AllowAnonymous]
    public IActionResult Start()
    {
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");

        _pkceStore[state] = codeVerifier;

        var callbackUri = BuildCallbackUri();
        var authorizeUrl = $"{IamIssuer}/connect/authorize" +
            $"?client_id={IamClientId}" +
            $"&response_type=code" +
            $"&scope=openid+profile+email+roles" +
            $"&redirect_uri={Uri.EscapeDataString(callbackUri)}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256";

        return Ok(new { authorizeUrl });
    }

    // GET /api/auth/sso/callback?code=...&state=...  (IAM redirects browser here)
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
    {
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("IAM SSO error: {Error}", error);
            return Redirect("/#sso_error=" + Uri.EscapeDataString(error));
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Redirect("/#sso_error=missing_params");

        if (!_pkceStore.TryRemove(state, out var codeVerifier))
            return Redirect("/#sso_error=invalid_state");

        var callbackUri = BuildCallbackUri();

        // Exchange code for IAM tokens
        var tokenResponse = await ExchangeCodeAsync(code, codeVerifier, callbackUri);
        if (tokenResponse == null)
            return Redirect("/#sso_error=token_exchange_failed");

        // Fetch user info from IAM
        var userInfo = await GetUserInfoAsync(tokenResponse.AccessToken);
        if (userInfo?.Email == null)
            return Redirect("/#sso_error=userinfo_failed");

        // Find or create local user
        var localUser = await _userManager.FindByEmailAsync(userInfo.Email);
        if (localUser == null)
        {
            localUser = new ApplicationUser
            {
                UserName = userInfo.Email,
                Email = userInfo.Email,
                FirstName = userInfo.GivenName ?? userInfo.Name?.Split(' ').FirstOrDefault() ?? "",
                LastName = userInfo.FamilyName ?? (userInfo.Name?.Contains(' ') == true
                    ? string.Join(' ', userInfo.Name.Split(' ').Skip(1))
                    : ""),
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                EmailConfirmed = true
            };
            var result = await _userManager.CreateAsync(localUser);
            if (!result.Succeeded)
            {
                _logger.LogError("Failed to create SSO user {Email}: {Errors}", userInfo.Email,
                    string.Join(", ", result.Errors.Select(e => e.Description)));
                return Redirect("/#sso_error=user_creation_failed");
            }
            await _userManager.AddToRoleAsync(localUser, "User");
        }

        if (!localUser.IsActive)
            return Redirect("/#sso_error=account_disabled");

        var roles = await _userManager.GetRolesAsync(localUser);
        var jwt = _jwtService.GenerateToken(localUser, roles);

        return Redirect($"/#sso_token={Uri.EscapeDataString(jwt)}");
    }

    private string BuildCallbackUri()
    {
        // Prefer explicit config (needed behind IIS URL Rewrite which doesn't forward Host)
        var baseUrl = _configuration["Sso:CallbackBaseUrl"];
        if (!string.IsNullOrEmpty(baseUrl))
            return $"{baseUrl.TrimEnd('/')}/api/auth/sso/callback";

        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
            ?? (Request.Host.Value.Contains("localhost") ? Request.Scheme : "https");
        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? Request.Host.Value;
        return $"{scheme}://{host}/api/auth/sso/callback";
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private async Task<IamTokenResponse?> ExchangeCodeAsync(string code, string codeVerifier, string callbackUri)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = callbackUri,
                ["client_id"] = IamClientId,
                ["code_verifier"] = codeVerifier
            };
            var response = await client.PostAsync($"{IamIssuer}/connect/token", new FormUrlEncodedContent(form));
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("IAM token exchange failed {Status}: {Body}", response.StatusCode, err);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<IamTokenResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token exchange exception");
            return null;
        }
    }

    private async Task<IamUserInfo?> GetUserInfoAsync(string accessToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await client.GetFromJsonAsync<IamUserInfo>($"{IamIssuer}/connect/userinfo");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Userinfo exception");
            return null;
        }
    }

    private sealed class IamTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("token_type")] public string TokenType { get; set; } = "";
    }

    private sealed class IamUserInfo
    {
        [JsonPropertyName("sub")] public string Sub { get; set; } = "";
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("given_name")] public string? GivenName { get; set; }
        [JsonPropertyName("family_name")] public string? FamilyName { get; set; }
    }
}
