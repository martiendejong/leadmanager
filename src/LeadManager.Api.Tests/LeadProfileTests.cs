using System.Security.Claims;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Enrichment;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>Lead UpdatedAt, sorting on created/updated, and the researched-profile patch with its derived score.</summary>
public class LeadProfileTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;

    public LeadProfileTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private LeadsController As(string userId)
    {
        var config = new ConfigurationBuilder().Build();
        var controller = new LeadsController(_db, null!, config, new EnrichmentChannel());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
            },
        };
        return controller;
    }

    private Lead Seed(string name, string owner = "me", DateTime? created = null)
    {
        var l = new Lead { Name = name, Website = $"https://{name.ToLower()}.example", ImportedByUserId = owner };
        if (created.HasValue) { l.CreatedAt = created.Value; l.ImportedAt = created.Value; l.UpdatedAt = created.Value; }
        _db.Leads.Add(l);
        _db.SaveChanges();
        return l;
    }

    private static PatchLeadProfileDto Patch(
        string? ownerName = null, string? ownerLinkedIn = null, string? ownerMobile = null, string? personalEmail = null,
        float? rating = null, int? year = null, string? employees = null, string? linkedIn = null, string? website = null,
        string? phone = null, string? description = null) =>
        new(null, website, null, null, phone, null, ownerName, null, null, null, ownerLinkedIn, ownerMobile, personalEmail, linkedIn,
            null, null, null, null, employees, year, rating, null, null, null, null, null, description, null, null, null, null, null, null, null, null, null);

    private async Task<LeadDto> Do(string user, Guid id, PatchLeadProfileDto dto) =>
        Assert.IsType<LeadDto>(Assert.IsType<OkObjectResult>(await As(user).PatchProfile(id, dto)).Value);

    [Fact]
    public async Task UpdatedAt_IsStampedOnEveryChange_AndNotOnReads()
    {
        var lead = Seed("Acme", created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), lead.UpdatedAt);

        var after = await Do("me", lead.Id, Patch(ownerName: "Jan Jansen"));
        Assert.True(after.UpdatedAt > new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(after.CreatedAt, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Sorting_ByCreatedAndUpdated_PutsTheNewestOnTop()
    {
        var oldest = Seed("Oldest", created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = Seed("Middle", created: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = Seed("Newest", created: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await Do("me", oldest.Id, Patch(ownerName: "Touched last"));   // the oldest lead is now the most recently updated

        async Task<string[]> Names(string sortBy, bool desc)
        {
            var result = Assert.IsType<OkObjectResult>(await As("me").GetLeads(new LeadFilterParams(SortBy: sortBy, SortDesc: desc)));
            var items = (IEnumerable<LeadDto>)result.Value!.GetType().GetProperty("items")!.GetValue(result.Value)!;
            return items.Select(i => i.Name).ToArray();
        }

        Assert.Equal(new[] { "Newest", "Middle", "Oldest" }, await Names("createdAt", true));
        Assert.Equal("Oldest", (await Names("updatedAt", true))[0]);
        Assert.Equal("Oldest", (await Names("createdAt", false))[0]);
    }

    [Fact]
    public async Task Patch_RecalculatesTheScoreFromTheProfile()
    {
        var lead = Seed("Acme");
        var d = await Do("me", lead.Id, Patch(ownerName: "Jan Jansen", ownerLinkedIn: "https://www.linkedin.com/in/jan-jansen", ownerMobile: "06 12345678"));
        Assert.Equal(5, d.SalesPriorityScore);   // owner LinkedIn 2 + mobile 2 + owner name 1

        d = await Do("me", lead.Id, Patch(rating: 4.8f, year: 2009, employees: "3", personalEmail: "jan@acme.example"));
        Assert.Equal(9, d.SalesPriorityScore);   // + rating 1 + established 1 + small 1 + personal email 1 = the maximum
        Assert.Equal("High", d.SalesPriorityLabel);
        Assert.Contains("Owner LinkedIn known", d.SalesPriorityReasoning);
    }

    [Fact]
    public async Task Patch_LeavesUntouchedFieldsAlone_AndEmptyStringClears()
    {
        var lead = Seed("Acme");
        await Do("me", lead.Id, Patch(ownerName: "Jan Jansen", description: "Roofing company"));
        var d = await Do("me", lead.Id, Patch(rating: 4.6f));
        Assert.Equal("Jan Jansen", d.OwnerName);
        Assert.Equal("Roofing company", d.Description);

        d = await Do("me", lead.Id, Patch(ownerName: ""));
        Assert.Equal("", d.OwnerName);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x.example")]
    public async Task Patch_RejectsUnsafeUrls(string url)
    {
        var lead = Seed("Acme");
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(ownerLinkedIn: url)));
    }

    [Fact]
    public async Task Patch_RejectsBadValues()
    {
        var lead = Seed("Acme");
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(rating: 7f)));
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(year: 1500)));
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(ownerMobile: "abc")));
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(personalEmail: "not-an-email")));
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(description: new string('x', 9000))));
    }

    [Fact]
    public async Task Patch_OfSomeoneElsesLead_IsNotFound()
    {
        var lead = Seed("Secret", owner: "other");
        Assert.IsType<NotFoundResult>(await As("me").PatchProfile(lead.Id, Patch(ownerName: "x")));
    }

    [Fact]
    public async Task Patch_ToANameAndWebsiteAnotherLeadHas_IsAConflict_NotAServerError()
    {
        var a = Seed("Acme");
        var b = Seed("Beta");
        var result = await As("me").PatchProfile(b.Id, Patch(website: a.Website));   // same website, different name: allowed
        Assert.IsType<OkObjectResult>(result);
        var clash = await As("me").PatchProfile(b.Id, new PatchLeadProfileDto("Acme", a.Website, null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        Assert.IsType<ConflictObjectResult>(clash);
    }

    [Fact]
    public async Task Patch_EmptyWebsite_IsRefused_AndBareAddressesGetHttps()
    {
        var lead = Seed("Acme");
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(website: "")));
        var d = await Do("me", lead.Id, Patch(ownerLinkedIn: "linkedin.com/in/jan-jansen"));
        Assert.Equal("https://linkedin.com/in/jan-jansen", d.OwnerLinkedInUrl);
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(personalEmail: "Jan <jan@acme.example>")));
        Assert.IsType<BadRequestObjectResult>(await As("me").PatchProfile(lead.Id, Patch(rating: float.NaN)));
    }

    [Fact]
    public async Task NormalEdit_RescoresToo()
    {
        var lead = Seed("Acme");
        await Do("me", lead.Id, Patch(ownerLinkedIn: "https://www.linkedin.com/in/jan"));
        var put = new UpdateLeadDto("Acme", lead.Website, "", "", "", "", "x", "Jan Jansen", "Jan", "Jansen", "jan@acme.example", "", null);
        var ok = Assert.IsType<OkObjectResult>(await As("me").UpdateLead(lead.Id, put));
        var dto = Assert.IsType<LeadDto>(ok.Value);
        Assert.Equal(4, dto.SalesPriorityScore);   // owner LinkedIn 2 + owner name 1 + personal e-mail 1
    }

    [Fact]
    public async Task Paging_IsStable_WhenManyLeadsShareTheSortValue()
    {
        for (var i = 0; i < 12; i++) Seed($"Lead{i:00}");   // none enriched: every row ties on isEnriched
        var seen = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = Assert.IsType<OkObjectResult>(await As("me").GetLeads(new LeadFilterParams(Page: page, PageSize: 5, SortBy: "isEnriched", SortDesc: true)));
            var items = (IEnumerable<LeadDto>)result.Value!.GetType().GetProperty("items")!.GetValue(result.Value)!;
            seen.AddRange(items.Select(i => i.Name));
        }
        Assert.Equal(12, seen.Distinct().Count());
    }
}
