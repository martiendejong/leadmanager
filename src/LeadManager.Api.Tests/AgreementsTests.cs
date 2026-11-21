using System.Security.Claims;
using System.Text;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>Services per client, contracts versus concepts overview, idempotent imports and duplicate files.</summary>
public class AgreementsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lm-agr-" + Guid.NewGuid().ToString("N"));
    private readonly DocumentStorage _storage;

    public AgreementsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
        _storage = new DocumentStorage(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Documents:Path"] = _root }).Build());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static ControllerContext Ctx(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
        },
    };

    private ServicesController Services(string user) => new(_db) { ControllerContext = Ctx(user) };
    private AgreementsController Agreements(string user) => new(_db) { ControllerContext = Ctx(user) };
    private DocumentsController Docs(string user) => new(_db, _storage) { ControllerContext = Ctx(user) };

    private Client SeedClient(string owner = "me", string name = "ThuisNotaris")
    {
        var c = new Client { Name = name, CreatedByUserId = owner };
        _db.Clients.Add(c);
        _db.SaveChanges();
        return c;
    }

    private static UpsertServiceDto Svc(string kind = "WebsiteManagement", string? status = "Active", string? sref = null) =>
        new(kind, null, status, null, null, 150m, null, null, sref);

    private static UpsertDocumentDto Doc(string kind, string title, string status, Guid? service = null, string? sref = null) =>
        new(kind, title, status, null, null, null, null, null, null, null, null, null, service, null, sref);

    private static T Val<T>(IActionResult r) => Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(r).Value);

    [Fact]
    public async Task Service_is_created_with_default_name_and_listed()
    {
        var c = SeedClient();
        var created = Val<ServiceDto>(await Services("me").Create(c.Id, Svc()));
        Assert.Equal("Website management", created.Name);
        Assert.Equal("Active", created.Status);
        var list = Assert.IsAssignableFrom<IEnumerable<ServiceDto>>(Assert.IsType<OkObjectResult>(await Services("me").List(c.Id)).Value);
        Assert.Single(list);
    }

    [Fact]
    public async Task Service_of_another_users_client_is_not_found()
    {
        var c = SeedClient("other");
        Assert.IsType<NotFoundResult>(await Services("me").Create(c.Id, Svc()));
        Assert.IsType<NotFoundResult>(await Services("me").List(c.Id));
    }

    [Fact]
    public async Task Same_source_ref_updates_instead_of_duplicating()
    {
        var c = SeedClient();
        await Services("me").Create(c.Id, Svc(status: "Proposed", sref: "fedha:contract:7"));
        var again = await Services("me").Create(c.Id, Svc(status: "Active", sref: "fedha:contract:7"));
        Assert.Equal("Active", Assert.IsType<OkObjectResult>(again).Value is ServiceDto d ? d.Status : "");
        Assert.Equal(1, await _db.ClientServices.CountAsync());
    }

    [Fact]
    public async Task Unknown_kind_and_bad_dates_are_rejected()
    {
        var c = SeedClient();
        Assert.IsType<BadRequestObjectResult>(await Services("me").Create(c.Id, Svc("Nonsense")));
        var bad = new UpsertServiceDto("Hosting", null, null, new DateTime(2026, 5, 1), new DateTime(2026, 4, 1), null, null, null);
        Assert.IsType<BadRequestObjectResult>(await Services("me").Create(c.Id, bad));
    }

    [Fact]
    public async Task Document_cannot_point_to_a_service_of_another_client()
    {
        var a = SeedClient(name: "A");
        var b = SeedClient(name: "B");
        var svc = Val<ServiceDto>(await Services("me").Create(b.Id, Svc()));
        Assert.IsType<BadRequestObjectResult>(await Docs("me").Create(a.Id, Doc("Agreement", "Contract", "Sent", svc.Id)));
        Assert.IsType<CreatedResult>(await Docs("me").Create(b.Id, Doc("Agreement", "Contract", "Sent", svc.Id)));
    }

    [Fact]
    public async Task Document_with_same_source_ref_is_updated_not_duplicated()
    {
        var c = SeedClient();
        await Docs("me").Create(c.Id, Doc("Nda", "NDA", "Draft", sref: "mail:123"));
        var second = await Docs("me").Create(c.Id, Doc("Nda", "NDA", "Sent", sref: "mail:123"));
        Assert.Equal("Sent", Val<DocumentDto>(second).Status);
        Assert.Equal(1, await _db.ClientDocuments.CountAsync());
    }

    [Fact]
    public async Task Overview_separates_concepts_from_contracts_and_filters_on_service()
    {
        var tn = SeedClient(name: "ThuisNotaris");
        var other = SeedClient(name: "Zonder dienst");
        var svc = Val<ServiceDto>(await Services("me").Create(tn.Id, Svc()));
        await Docs("me").Create(tn.Id, Doc("Nda", "Vertrouwelijkheidsverklaring", "Draft"));
        await Docs("me").Create(tn.Id, Doc("Dpa", "Verwerkersovereenkomst", "Draft"));
        await Docs("me").Create(tn.Id, Doc("Agreement", "Beheerovereenkomst", "Sent", svc.Id));
        await Docs("me").Create(other.Id, Doc("Agreement", "Oud contract", "Signed"));

        var all = Assert.IsAssignableFrom<List<ClientAgreementsDto>>(Assert.IsType<OkObjectResult>(await Agreements("me").Overview(null)).Value);
        Assert.Equal(2, all.Count);
        var row = all.Single(x => x.ClientName == "ThuisNotaris");
        Assert.Equal(2, row.Concepts.Count);
        Assert.Single(row.Contracts);
        Assert.Single(row.Services);

        var only = Assert.IsAssignableFrom<List<ClientAgreementsDto>>(Assert.IsType<OkObjectResult>(await Agreements("me").Overview("WebsiteManagement")).Value);
        Assert.Equal("ThuisNotaris", Assert.Single(only).ClientName);

        Assert.IsType<BadRequestObjectResult>(await Agreements("me").Overview("Nope"));
        Assert.Empty(Assert.IsAssignableFrom<List<ClientAgreementsDto>>(Assert.IsType<OkObjectResult>(await Agreements("someone-else").Overview(null)).Value));
    }

    [Fact]
    public async Task Ended_service_does_not_count_as_website_management()
    {
        var c = SeedClient();
        await Services("me").Create(c.Id, Svc(status: "Ended"));
        var only = Assert.IsAssignableFrom<List<ClientAgreementsDto>>(Assert.IsType<OkObjectResult>(await Agreements("me").Overview("WebsiteManagement")).Value);
        Assert.Empty(only);
    }

    [Fact]
    public async Task Identical_file_on_same_document_is_linked_once()
    {
        var c = SeedClient();
        var doc = Val<DocumentDto>(await Docs("me").Create(c.Id, Doc("Nda", "NDA", "Draft")));
        IFormFile F() { var b = Encoding.UTF8.GetBytes("%PDF-1.4 same"); return new FormFile(new MemoryStream(b), 0, b.Length, "file", "nda.pdf"); }
        var first = await Docs("me").Upload(doc.Id, F(), null, default);
        var second = await Docs("me").Upload(doc.Id, F(), null, default);
        Assert.IsType<CreatedResult>(first);
        Assert.IsType<OkObjectResult>(second);
        Assert.Equal(1, await _db.ClientDocumentFiles.CountAsync());
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }
}
