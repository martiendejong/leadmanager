using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JengoComponents.McpConnector;
using LeadManager.Api.Data;
using LeadManager.Api.Mcp;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>The OAuth authorization server in front of /mcp (ChatGPT and Claude.ai custom connectors).</summary>
public class McpOAuthTests : IDisposable
{
    private const string ChatGptRedirect = "https://chatgpt.com/connector_platform_oauth_redirect";
    private const string ClaudeRedirect = "https://claude.ai/api/mcp/auth_callback";
    private const string ClaudeComRedirect = "https://claude.com/api/mcp/auth_callback";

    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly McpConnectorOptions _options = new() { ServerName = "jengo-clients", Realm = "JengoClients" };
    private readonly OAuthService _oauth;

    public McpOAuthTests()
    {
        _options.ExtraAllowedRedirectHosts.Add("claude.com");
        _oauth = new OAuthService(_options);
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- plumbing ------------------------------------------------------------------------------------

    private LeadManagerOAuthController Controller(string? userId = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("clients.example.test");
        http.Request.Scheme = "https";
        if (userId != null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Bearer"));
        return new LeadManagerOAuthController(_options, _oauth, _db)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private string SeedUser(string email, bool active = true)
    {
        var user = new ApplicationUser { UserName = email, Email = email, FirstName = "Test", LastName = email, IsActive = active };
        _db.Users.Add(user);
        _db.SaveChanges();
        TestOrgInterceptor.Join(_db, user.Id);
        return user.Id;
    }

    private string AddKey(string userId, string name)
    {
        var key = ApiKeyDefaults.Generate();
        _db.ApiKeys.Add(new ApiKey { Name = name, KeyHash = ApiKeyDefaults.Hash(key), Prefix = key[..8], UserId = userId });
        _db.SaveChanges();
        return key;
    }

    private static (string verifier, string challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return (verifier, Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    private string Register(string redirect)
    {
        var result = Controller().Register(new ClientRegistrationRequest { ClientName = "ChatGPT", RedirectUris = [redirect] });
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        return json.GetProperty("client_id").GetString()!;
    }

    private static string CodeFrom(string redirect) =>
        Uri.UnescapeDataString(redirect.Split("code=")[1].Split('&')[0]);

    private async Task<string> Authorize(string userId, string redirect, string challenge, string state = "xyz")
    {
        var clientId = Register(redirect);
        var result = await Controller(userId).Consent(new LeadManagerOAuthController.ConsentSubmit
        {
            ClientId = clientId, RedirectUri = redirect, State = state,
            CodeChallenge = challenge, CodeChallengeMethod = "S256",
        }, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        var back = json.GetProperty("redirect").GetString()!;
        Assert.StartsWith(redirect, back);
        Assert.Contains("state=" + state, back);
        // The auth code is bound to the client; the token request below needs the same client id.
        return back + "&client_id=" + clientId;
    }

    private IActionResult Exchange(string redirectWithCode, string redirect, string verifier)
    {
        var clientId = redirectWithCode.Split("&client_id=")[1];
        return Controller().Token(new TokenRequest
        {
            GrantType = "authorization_code", Code = CodeFrom(redirectWithCode), CodeVerifier = verifier,
            RedirectUri = redirect, ClientId = clientId,
        });
    }

    // ---- discovery and registration -----------------------------------------------------------------

    [Fact]
    public void Discovery_DocumentsPointAtThisHost_AndAdvertiseRegistration()
    {
        var c = Controller();
        var rm = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(c.ProtectedResourceMetadata()).Value);
        Assert.Equal("https://clients.example.test/mcp", rm.GetProperty("resource").GetString());
        Assert.Equal("https://clients.example.test", rm.GetProperty("authorization_servers")[0].GetString());

        var asm = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(c.AuthorizationServerMetadata()).Value);
        Assert.Equal("https://clients.example.test/oauth/register", asm.GetProperty("registration_endpoint").GetString());
        Assert.Equal("https://clients.example.test/oauth/authorize", asm.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("https://clients.example.test/oauth/token", asm.GetProperty("token_endpoint").GetString());
        Assert.Equal("S256", asm.GetProperty("code_challenge_methods_supported")[0].GetString());
    }

    [Theory]
    [InlineData(ChatGptRedirect)]
    [InlineData(ClaudeRedirect)]
    [InlineData(ClaudeComRedirect)]
    public void Registration_AcceptsChatGptAndClaudeRedirects(string redirect)
    {
        Assert.StartsWith("gpt_", Register(redirect));
    }

    [Theory]
    [InlineData("https://evil.example/cb")]
    [InlineData("https://chatgpt.com.evil.example/cb")]
    [InlineData("http://chatgpt.com/cb")]
    [InlineData("https://notclaude.com/cb")]
    public void Registration_RefusesOtherRedirects(string redirect)
    {
        var result = Controller().Register(new ClientRegistrationRequest { RedirectUris = [redirect] });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ---- consent -------------------------------------------------------------------------------------

    [Fact]
    public async Task AuthorizePage_RequiresPkce_AndRendersTheConsentPage()
    {
        var clientId = Register(ChatGptRedirect);
        var (_, challenge) = Pkce();

        var noPkce = await Controller().AuthorizePage(clientId, ChatGptRedirect, "s", null, null);
        Assert.IsType<BadRequestObjectResult>(noPkce);

        var ok = await Controller().AuthorizePage(clientId, ChatGptRedirect, "s", challenge, "S256");
        var content = Assert.IsType<ContentResult>(ok);
        Assert.Contains("lm_token", content.Content);
        Assert.Contains(challenge, content.Content);
        Assert.DoesNotContain("__PARAMS__", content.Content);

        var evil = await Controller().AuthorizePage(clientId, "https://evil.example/cb", "s", challenge, "S256");
        Assert.IsType<BadRequestObjectResult>(evil);
    }

    [Fact]
    public async Task AuthorizePage_EscapesAttackerControlledValues_InsideTheScript()
    {
        var clientId = Register(ChatGptRedirect);
        var (_, challenge) = Pkce();

        var ok = await Controller().AuthorizePage(clientId, ChatGptRedirect, "</script><script>alert(1)</script>", challenge, "S256");

        var html = Assert.IsType<ContentResult>(ok).Content!;
        Assert.DoesNotContain("</script><script>alert(1)", html);
    }

    [Fact]
    public async Task Consent_MintsAPersonalKey_AndRotatesTheOldConnectorKey()
    {
        var userId = SeedUser("owner@example.test");
        var other = AddKey(userId, "My script");
        var first = AddKey(userId, LeadManagerOAuthController.ChatGptKeyName);
        var (_, challenge) = Pkce();

        await Authorize(userId, ChatGptRedirect, challenge);

        var keys = await _db.ApiKeys.Where(k => k.UserId == userId).ToListAsync();
        Assert.Equal(3, keys.Count);
        Assert.Single(keys, k => k.Name == LeadManagerOAuthController.ChatGptKeyName && k.RevokedAt == null);
        Assert.NotNull(keys.Single(k => k.KeyHash == ApiKeyDefaults.Hash(first)).RevokedAt);
        // existing lm_ keys keep working
        var validator = new ApiKeyTokenValidator(_db);
        Assert.Null(await validator.ValidateAsync(first));
        Assert.NotNull(await validator.ValidateAsync(other));
    }

    [Fact]
    public async Task Consent_KeyNamesFollowTheClient_SoChatGptAndClaudeDoNotRotateEachOther()
    {
        var userId = SeedUser("both@example.test");
        var (_, challenge) = Pkce();

        await Authorize(userId, ChatGptRedirect, challenge);
        await Authorize(userId, ClaudeRedirect, challenge);

        var live = await _db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null).Select(k => k.Name).ToListAsync();
        Assert.Contains(LeadManagerOAuthController.ChatGptKeyName, live);
        Assert.Contains(LeadManagerOAuthController.ClaudeKeyName, live);
        Assert.Equal(LeadManagerOAuthController.ClaudeKeyName, LeadManagerOAuthController.KeyNameFor(ClaudeComRedirect));
    }

    [Fact]
    public async Task Consent_RefusesInactiveUsers_OffAllowlistRedirects_AndMissingPkce()
    {
        var inactive = SeedUser("gone@example.test", active: false);
        var active = SeedUser("here@example.test");
        var (_, challenge) = Pkce();
        var clientId = Register(ChatGptRedirect);

        var r1 = await Controller(inactive).Consent(new() { ClientId = clientId, RedirectUri = ChatGptRedirect, CodeChallenge = challenge, CodeChallengeMethod = "S256" }, default);
        Assert.IsType<UnauthorizedObjectResult>(r1);

        var r2 = await Controller(active).Consent(new() { ClientId = clientId, RedirectUri = "https://evil.example/cb", CodeChallenge = challenge, CodeChallengeMethod = "S256" }, default);
        Assert.IsType<BadRequestObjectResult>(r2);

        var r3 = await Controller(active).Consent(new() { ClientId = clientId, RedirectUri = ChatGptRedirect }, default);
        Assert.IsType<BadRequestObjectResult>(r3);

        var r4 = await Controller(null).Consent(new() { ClientId = clientId, RedirectUri = ChatGptRedirect, CodeChallenge = challenge, CodeChallengeMethod = "S256" }, default);
        Assert.IsType<UnauthorizedObjectResult>(r4);

        Assert.Empty(await _db.ApiKeys.ToListAsync());
    }

    // ---- token exchange and the scoped tools ---------------------------------------------------------

    [Fact]
    public async Task FullDance_CodePlusPkce_GivesAKeyThatActsAsThatUser_AndNoOneElse()
    {
        var alice = SeedUser("alice@example.test");
        var bob = SeedUser("bob@example.test");
        var (verifier, challenge) = Pkce();

        var back = await Authorize(alice, ChatGptRedirect, challenge);
        var token = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(Exchange(back, ChatGptRedirect, verifier)).Value);
        Assert.Equal("Bearer", token.GetProperty("token_type").GetString());
        var accessToken = token.GetProperty("access_token").GetString()!;
        Assert.StartsWith("lm_", accessToken);

        var principal = await new ApiKeyTokenValidator(_db).ValidateAsync(accessToken);
        Assert.NotNull(principal);
        Assert.Equal(alice, principal!.Subject);
        Assert.NotEqual(bob, principal.Subject);

        // The code is single use.
        Assert.IsType<BadRequestObjectResult>(Exchange(back, ChatGptRedirect, verifier));
    }

    [Fact]
    public async Task Token_RefusesAWrongVerifier_AWrongRedirect_AndAWrongClient()
    {
        var user = SeedUser("pkce@example.test");
        var (verifier, challenge) = Pkce();

        var a = await Authorize(user, ChatGptRedirect, challenge);
        Assert.IsType<BadRequestObjectResult>(Exchange(a, ChatGptRedirect, "not-the-verifier"));

        var b = await Authorize(user, ChatGptRedirect, challenge);
        Assert.IsType<BadRequestObjectResult>(Exchange(b, ClaudeRedirect, verifier));

        var c = await Authorize(user, ChatGptRedirect, challenge);
        var wrongClient = Controller().Token(new TokenRequest
        {
            GrantType = "authorization_code", Code = CodeFrom(c), CodeVerifier = verifier,
            RedirectUri = ChatGptRedirect, ClientId = "gpt_someoneelse",
        });
        Assert.IsType<BadRequestObjectResult>(wrongClient);
    }
}
