using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LeadManager.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeadManager.Api.Services;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string SmartScheme = "JwtOrApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string KeyPrefix = "lm_";

    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Generates "lm_" + 32 random bytes, base64url encoded.</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return KeyPrefix + b64;
    }
}

/// <summary>
/// Authenticates agents and other Jengo tools by the X-Api-Key header. The key acts as the
/// user that created it, so the existing per-user ownership rules keep applying unchanged.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly LeadManagerDbContext _db;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        LeadManagerDbContext db) : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var header))
            return AuthenticateResult.NoResult();

        var key = header.ToString().Trim();
        if (!key.StartsWith(ApiKeyDefaults.KeyPrefix, StringComparison.Ordinal))
            return AuthenticateResult.Fail("Invalid API key");

        var hash = ApiKeyDefaults.Hash(key);
        var record = await _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null);
        if (record == null)
            return AuthenticateResult.Fail("Invalid or revoked API key");

        var now = DateTime.UtcNow;
        if (record.LastUsedAt == null || now - record.LastUsedAt > TimeSpan.FromMinutes(5))
        {
            record.LastUsedAt = now;
            await _db.SaveChangesAsync();
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, record.UserId),
            new Claim(ClaimTypes.Name, $"api-key:{record.Name}"),
            new Claim("auth_method", "api_key"),
        }, Scheme.Name);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
