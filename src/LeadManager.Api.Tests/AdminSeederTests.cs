using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>
/// JengoWork task 8028: admin accounts come from configuration only. The old code created two
/// admins with a password that was committed to git and reset it on every start.
/// </summary>
public class AdminSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public AdminSeederTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LeadManagerDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            // Same rules as Program.cs
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireNonAlphanumeric = true;
            options.User.RequireUniqueEmail = true;
        }).AddEntityFrameworkStores<LeadManagerDbContext>().AddDefaultTokenProviders();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>().Database.EnsureCreated();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var r in new[] { "Admin", "User" })
            if (!roles.RoleExistsAsync(r).GetAwaiter().GetResult())
                roles.CreateAsync(new IdentityRole(r)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private async Task Seed(IConfiguration config)
    {
        using var scope = _provider.CreateScope();
        await AdminSeeder.SeedAsync(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(), config, NullLogger.Instance);
    }

    private async Task<ApplicationUser?> Find(string email)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }

    private async Task<bool> PasswordWorks(string email, string password)
    {
        using var scope = _provider.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByEmailAsync(email);
        return user != null && await um.CheckPasswordAsync(user, password);
    }

    [Fact]
    public async Task WithoutConfiguration_NoAccountIsCreated()
    {
        await Seed(Config());

        using var scope = _provider.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users);
    }

    [Fact]
    public async Task ConfiguredAdmin_IsCreatedWithAdminRole_AndTheConfiguredPassword()
    {
        await Seed(Config(
            ("Seed:Admins:0:Email", "admin@example.test"),
            ("Seed:Admins:0:Password", "Config-Secret-9!"),
            ("Seed:Admins:0:FirstName", "Ada")));

        var user = await Find("admin@example.test");
        Assert.NotNull(user);
        Assert.Equal("Ada", user!.FirstName);
        Assert.True(await PasswordWorks("admin@example.test", "Config-Secret-9!"));

        using var scope = _provider.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().IsInRoleAsync(user, "Admin"));
    }

    [Fact]
    public async Task OldHardcodedPassword_NoLongerWorks_WhenNothingIsConfigured()
    {
        await Seed(Config());

        Assert.Null(await Find("info@martiendejong.nl"));
        Assert.Null(await Find("info@prospergenics.com"));
    }

    [Fact]
    public async Task ExistingAdmin_KeepsTheirPassword_OnRestart()
    {
        var first = Config(("Seed:Admins:0:Email", "a@example.test"), ("Seed:Admins:0:Password", "First-Secret-1!"));
        await Seed(first);

        // The owner changes the password in the app, then the service restarts with the old config.
        using (var scope = _provider.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await um.FindByEmailAsync("a@example.test"))!;
            Assert.True((await um.ChangePasswordAsync(user, "First-Secret-1!", "Changed-By-Owner-2!")).Succeeded);
        }
        await Seed(first);

        Assert.True(await PasswordWorks("a@example.test", "Changed-By-Owner-2!"));
        Assert.False(await PasswordWorks("a@example.test", "First-Secret-1!"));
    }

    [Fact]
    public async Task ResetFlag_ResetsAnExistingPassword_ForRecovery()
    {
        await Seed(Config(("Seed:Admins:0:Email", "a@example.test"), ("Seed:Admins:0:Password", "First-Secret-1!")));

        await Seed(Config(
            ("Seed:Admins:0:Email", "a@example.test"),
            ("Seed:Admins:0:Password", "Recovery-Secret-3!"),
            ("Seed:ResetExistingPasswords", "true")));

        Assert.True(await PasswordWorks("a@example.test", "Recovery-Secret-3!"));
    }

    [Fact]
    public async Task ExistingNormalUser_GainsAdminRole_WhenConfiguredAsAdmin_WithoutPasswordChange()
    {
        using (var scope = _provider.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await um.CreateAsync(new ApplicationUser { UserName = "u@example.test", Email = "u@example.test", FirstName = "U", LastName = "" }, "User-Secret-5!");
        }

        await Seed(Config(("Seed:Admins:0:Email", "u@example.test"), ("Seed:Admins:0:Password", "Other-Secret-7!")));

        using var check = _provider.CreateScope();
        var manager = check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await manager.FindByEmailAsync("u@example.test"))!;
        Assert.True(await manager.IsInRoleAsync(user, "Admin"));
        Assert.True(await manager.CheckPasswordAsync(user, "User-Secret-5!"));
    }

    [Fact]
    public async Task WeakPassword_IsRefused_WithoutThrowing_AndNoAccountExists()
    {
        await Seed(Config(("Seed:Admins:0:Email", "weak@example.test"), ("Seed:Admins:0:Password", "short")));

        Assert.Null(await Find("weak@example.test"));
    }

    [Fact]
    public async Task IncompleteEntries_AreSkipped_AndOthersStillSeeded()
    {
        await Seed(Config(
            ("Seed:Admins:0:Email", "nopass@example.test"),
            ("Seed:Admins:1:Password", "Orphan-Secret-1!"),
            ("Seed:Admins:2:Email", "ok@example.test"),
            ("Seed:Admins:2:Password", "Fine-Secret-1!")));

        Assert.Null(await Find("nopass@example.test"));
        Assert.NotNull(await Find("ok@example.test"));
    }

    [Fact]
    public void SourceCode_ContainsNoHardcodedAdminPassword()
    {
        // Regression guard: the password was committed to git. Walk up to the repo's src folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "LeadManager.Api")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var program = File.ReadAllText(Path.Combine(dir!.FullName, "LeadManager.Api", "Program.cs"));
        Assert.DoesNotContain("SpaceElevator", program);
        Assert.DoesNotContain("Password:", program.Replace("options.Password.", ""));
    }
}
