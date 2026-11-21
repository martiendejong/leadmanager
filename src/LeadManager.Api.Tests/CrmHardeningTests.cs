using System.Security.Claims;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>JengoWork task 8068: the low-severity points of the independent review of PR 48, 49 and 50.</summary>
public class CrmHardeningTests : IDisposable
{
    private const string Me = "user-me";

    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;

    public CrmHardeningTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
        TestOrgInterceptor.Join(_db, Me);
        _access = new CrmParentAccess(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private T As<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Me) }, "Test")),
            },
        };
        return controller;
    }

    private Lead SeedLead(string name = "Hogenkamp", string website = "https://hogenkamp.nl", string email = "info@hogenkamp.nl")
    {
        var lead = new Lead { Name = name, Website = website, ImportedByUserId = Me, CompanyEmail = email };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        return lead;
    }

    // ---- DueAt ----------------------------------------------------------------------------

    [Fact]
    public async Task NextAction_WithoutDueAt_IsRejected_NotStoredAsYear0001()
    {
        var lead = SeedLead();
        var dto = System.Text.Json.JsonSerializer.Deserialize<CreateNextActionDto>("{\"Title\":\"Bellen\"}")!;

        var result = await As(new NextActionsController(_db, _access)).Create("leads", lead.Id, dto);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.NextActions);
    }

    [Fact]
    public async Task NextAction_Update_RejectsAnEmptyDate()
    {
        var lead = SeedLead();
        var c = As(new NextActionsController(_db, _access));
        var created = Assert.IsType<NextActionDto>(Assert.IsType<CreatedAtActionResult>(await c.Create("leads", lead.Id,
            new CreateNextActionDto("Bellen", DateTime.UtcNow.AddDays(1), null, null))).Value);

        var result = await c.Update(created.Id, new UpdateNextActionDto(null, default(DateTime), null));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Import_ActionWithoutDueAt_IsSkippedWithAProblem()
    {
        var rows = new ImportRequestDto(new List<ImportLeadDto>
        {
            new("Rein", "https://rein.nl", null, null, null, null, null, null, null,
                new List<ImportNextActionDto> { new("Bellen", default) }),
        });

        var result = Assert.IsType<OkObjectResult>(await As(new CrmImportController(_db, _access)).Import(rows, dryRun: false));
        var report = Assert.IsType<ImportReportDto>(result.Value);

        Assert.Contains(report.Leads[0].Problems, p => p.Contains("DueAt is required"));
        Assert.Empty(_db.NextActions);
    }

    // ---- Enum lists -------------------------------------------------------------------------

    [Theory]
    [InlineData("Call,Email")]
    [InlineData("Call, Email")]
    [InlineData("Email,Note")]
    public void TryParseName_RejectsCommaLists(string input) =>
        Assert.False(CrmRules.TryParseName<InteractionKind>(input, out _));

    // ---- Import: nulls and lengths ----------------------------------------------------------

    [Fact]
    public async Task Import_NullEntries_AreABadRequest_NotAServerError()
    {
        var import = As(new CrmImportController(_db, _access));

        Assert.IsType<BadRequestObjectResult>(await import.Import(new ImportRequestDto(new List<ImportLeadDto?> { null }!), true));
        Assert.IsType<BadRequestObjectResult>(await import.Import(new ImportRequestDto(new List<ImportLeadDto>
        {
            new("A", "https://a.nl", null, null, null, null, null, new List<ImportContactDto?> { null }!, null, null),
        }), true));
        Assert.IsType<BadRequestObjectResult>(await import.Import(new ImportRequestDto(new List<ImportLeadDto>
        {
            new("A", "https://a.nl", null, null, null, null, null, null, new List<ImportInteractionDto?> { null }!, null),
        }), true));
        Assert.IsType<BadRequestObjectResult>(await import.Import(new ImportRequestDto(new List<ImportLeadDto>
        {
            new("A", "https://a.nl", null, null, null, null, null, null, null, new List<ImportNextActionDto?> { null }!),
        }), true));
    }

    [Theory]
    [InlineData("Sector")]
    [InlineData("City")]
    [InlineData("Phone")]
    [InlineData("Email")]
    [InlineData("Source")]
    public async Task Import_OverlongLeadFields_AreReportedAsInvalid(string field)
    {
        var tooLong = new string('x', CrmRules.MaxName + 1);
        var lead = new ImportLeadDto("Long", "https://long.nl",
            field == "Sector" ? tooLong : null, field == "City" ? tooLong : null, field == "Phone" ? tooLong : null,
            field == "Email" ? tooLong : null, field == "Source" ? tooLong : null, null, null, null);

        var result = Assert.IsType<OkObjectResult>(await As(new CrmImportController(_db, _access)).Import(new ImportRequestDto(new List<ImportLeadDto> { lead }), false));
        var report = Assert.IsType<ImportReportDto>(result.Value);

        Assert.Equal("invalid", report.Leads[0].Action);
        Assert.Contains(report.Leads[0].Problems, p => p.StartsWith(field));
        Assert.Empty(_db.Leads);
    }

    // ---- Search ordering ------------------------------------------------------------------

    [Fact]
    public async Task Find_ExactEmailMatch_IsNotPushedOutByManyNameMatches()
    {
        // 250 leads whose name matches the query, more than the 200 candidates, plus one exact email match
        // whose name does not match. Without ordering the exact match could fall outside the cap.
        for (var i = 0; i < 250; i++)
            _db.Leads.Add(new Lead { Name = $"Advocaten {i}", Website = $"https://adv{i}.nl", ImportedByUserId = Me });
        var exact = new Lead { Name = "Zzz Kantoor", Website = "https://zzz.nl", ImportedByUserId = Me, CompanyEmail = "werner@zzz.nl" };
        _db.Leads.Add(exact);
        _db.SaveChanges();

        var rows = Assert.IsAssignableFrom<IEnumerable<CrmMatchDto>>(Assert.IsType<OkObjectResult>(
            await As(new CrmSearchController(_db, _access)).Find("werner@zzz.nl", null, "advocaten")).Value).ToList();

        Assert.Equal(exact.Id, rows[0].ParentId);
        Assert.Equal("email", rows[0].MatchedOn);
    }

    // ---- Admin seeder recovery ---------------------------------------------------------------

    [Fact]
    public async Task ResetFlag_AlsoReactivatesAndUnlocksTheAdmin()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LeadManagerDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireNonAlphanumeric = true;
        }).AddEntityFrameworkStores<LeadManagerDbContext>().AddDefaultTokenProviders();
        await using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await roles.CreateAsync(new IdentityRole("Admin"));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var first = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:Admins:0:Email"] = "a@example.test", ["Seed:Admins:0:Password"] = "First-Secret-1!",
        }).Build();
        await AdminSeeder.SeedAsync(users, first, NullLogger.Instance);

        // The admin got deactivated and locked out (too many wrong passwords).
        var admin = (await users.FindByEmailAsync("a@example.test"))!;
        admin.IsActive = false;
        await users.UpdateAsync(admin);
        await users.SetLockoutEnabledAsync(admin, true);
        await users.SetLockoutEndDateAsync(admin, DateTimeOffset.UtcNow.AddDays(1));
        await users.AccessFailedAsync(admin);

        var recovery = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:Admins:0:Email"] = "a@example.test", ["Seed:Admins:0:Password"] = "Recovery-Secret-3!",
            ["Seed:ResetExistingPasswords"] = "true",
        }).Build();
        await AdminSeeder.SeedAsync(users, recovery, NullLogger.Instance);

        var recovered = (await users.FindByEmailAsync("a@example.test"))!;
        Assert.True(recovered.IsActive);
        Assert.False(await users.IsLockedOutAsync(recovered));
        Assert.Equal(0, await users.GetAccessFailedCountAsync(recovered));
        Assert.True(await users.CheckPasswordAsync(recovered, "Recovery-Secret-3!"));
    }
}
