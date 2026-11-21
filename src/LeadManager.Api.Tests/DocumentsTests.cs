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

/// <summary>The dossier of a client: quotes, orders, signed agreements, with their files.</summary>
public class DocumentsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lm-docs-" + Guid.NewGuid().ToString("N"));
    private readonly DocumentStorage _storage;

    public DocumentsTests()
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

    private DocumentsController As(string userId) => new(_db, _storage)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
            },
        },
    };

    private Client SeedClient(string owner = "me", string name = "ThuisNotaris")
    {
        var c = new Client { Name = name, CreatedByUserId = owner };
        _db.Clients.Add(c);
        _db.SaveChanges();
        return c;
    }

    private static UpsertDocumentDto Doc(string kind = "Quote", string title = "Offerte AI-workspace", string? status = null,
        Guid? predecessor = null, Guid? project = null, decimal? amount = null) =>
        new(kind, title, status, "OFF-0042", null, null, null, amount, null, null, project, predecessor);

    private static IFormFile File(string name, string content = "%PDF-1.4 test")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }

    private async Task<DocumentDto> Create(string user, Guid clientId, UpsertDocumentDto dto)
    {
        var created = Assert.IsType<CreatedResult>(await As(user).Create(clientId, dto));
        return Assert.IsType<DocumentDto>(created.Value);
    }

    [Fact]
    public async Task Create_StoresDocumentForTheOwnersClient_OtherUsersGetNotFound()
    {
        var client = SeedClient();
        var doc = await Create("me", client.Id, Doc(amount: 1500.499m));
        Assert.Equal("Quote", doc.Kind);
        Assert.Equal("Draft", doc.Status);
        Assert.Equal(1500.50m, doc.Amount);

        Assert.IsType<NotFoundResult>(await As("other").Create(client.Id, Doc()));
        Assert.IsType<NotFoundResult>(await As("other").List(client.Id));
    }

    [Fact]
    public async Task Signed_WithoutDate_GetsASignedDate()
    {
        var doc = await Create("me", SeedClient().Id, Doc("Dpa", "Verwerkersovereenkomst", "Signed"));
        Assert.NotNull(doc.SignedAt);
    }

    [Theory]
    [InlineData("Bogus", "x", null)]
    [InlineData("Quote", "  ", null)]
    [InlineData("Quote", "ok", "Bogus")]
    public async Task Create_RejectsBadInput(string kind, string title, string? status) =>
        Assert.IsType<BadRequestObjectResult>(await As("me").Create(SeedClient().Id, Doc(kind, title, status)));

    [Fact]
    public async Task Create_RejectsUnsafeUrlAndNegativeAmount()
    {
        var client = SeedClient();
        var badUrl = Doc() with { Url = "javascript:alert(1)" };
        var negative = Doc() with { Amount = -5 };
        Assert.IsType<BadRequestObjectResult>(await As("me").Create(client.Id, badUrl));
        Assert.IsType<BadRequestObjectResult>(await As("me").Create(client.Id, negative));
    }

    [Fact]
    public async Task Chain_QuoteOrderAgreement_AndNoLoopsOrForeignLinks()
    {
        var client = SeedClient();
        var quote = await Create("me", client.Id, Doc("Quote"));
        var order = await Create("me", client.Id, Doc("Order", "Opdracht", predecessor: quote.Id));
        var agreement = await Create("me", client.Id, Doc("Agreement", "Overeenkomst", predecessor: order.Id));
        Assert.Equal(order.Id, agreement.PredecessorId);

        // The quote cannot follow from the agreement: that would close a loop.
        Assert.IsType<BadRequestObjectResult>(await As("me").Update(quote.Id, Doc("Quote", predecessor: agreement.Id)));
        // Nor from itself.
        Assert.IsType<BadRequestObjectResult>(await As("me").Update(quote.Id, Doc("Quote", predecessor: quote.Id)));

        // A document of another client cannot be a predecessor.
        var other = SeedClient("me", "Andere klant");
        var foreign = await Create("me", other.Id, Doc("Quote"));
        Assert.IsType<BadRequestObjectResult>(await As("me").Create(client.Id, Doc("Order", predecessor: foreign.Id)));
    }

    [Fact]
    public async Task Project_MustBelongToTheSameClient()
    {
        var client = SeedClient();
        var other = SeedClient("me", "Andere klant");
        var project = new Project { ClientId = other.Id, Name = "Elders" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await As("me").Create(client.Id, Doc(project: project.Id)));
    }

    [Fact]
    public async Task Upload_StoresTheFile_DownloadReturnsTheSameBytes_AsAttachment()
    {
        var doc = await Create("me", SeedClient().Id, Doc("Dpa", "Verwerkersovereenkomst"));
        var up = Assert.IsType<CreatedResult>(await As("me").Upload(doc.Id, File("DPA getekend.pdf"), "getekend", CancellationToken.None));
        var file = Assert.IsType<DocumentFileDto>(up.Value);
        Assert.Equal("application/pdf", file.ContentType);

        var controller = As("me");
        var result = Assert.IsType<FileStreamResult>(await controller.Download(file.Id));
        using var reader = new StreamReader(result.FileStream);
        Assert.Equal("%PDF-1.4 test", await reader.ReadToEndAsync());
        Assert.Equal("DPA getekend.pdf", result.FileDownloadName);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());

        var list = Assert.IsAssignableFrom<IEnumerable<DocumentDto>>(Assert.IsType<OkObjectResult>(await As("me").List(doc.ClientId)).Value).Single();
        Assert.Single(list.Files);
    }

    [Fact]
    public async Task Upload_RefusesUnknownTypes_AndOtherUsers()
    {
        var doc = await Create("me", SeedClient().Id, Doc());
        Assert.IsType<BadRequestObjectResult>(await As("me").Upload(doc.Id, File("virus.exe"), null, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await As("me").Upload(doc.Id, File("page.html"), null, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await As("me").Upload(doc.Id, null, null, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await As("other").Upload(doc.Id, File("a.pdf"), null, CancellationToken.None));
        Assert.Empty(await _db.ClientDocumentFiles.ToListAsync());
    }

    [Fact]
    public async Task Download_OfSomeoneElsesFile_IsNotFound()
    {
        var doc = await Create("me", SeedClient().Id, Doc());
        var file = Assert.IsType<DocumentFileDto>(Assert.IsType<CreatedResult>(await As("me").Upload(doc.Id, File("a.pdf"), null, CancellationToken.None)).Value);
        Assert.IsType<NotFoundResult>(await As("other").Download(file.Id));
        Assert.IsType<NotFoundResult>(await As("other").DeleteFile(file.Id));
    }

    [Fact]
    public async Task UserFileNames_NeverReachTheDisk()
    {
        var doc = await Create("me", SeedClient().Id, Doc());
        var up = Assert.IsType<CreatedResult>(await As("me").Upload(doc.Id, File(@"..\..\evil\..\contract.pdf"), null, CancellationToken.None));
        var file = Assert.IsType<DocumentFileDto>(up.Value);
        Assert.Equal("contract.pdf", file.FileName);
        var row = await _db.ClientDocumentFiles.SingleAsync();
        Assert.DoesNotContain("contract", row.StoragePath);
        Assert.DoesNotContain("..", row.StoragePath);
        Assert.All(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), f => Assert.StartsWith(_root, Path.GetFullPath(f)));
    }

    [Fact]
    public async Task DeleteDocument_RemovesFilesFromDisk_AndKeepsFollowers()
    {
        var client = SeedClient();
        var quote = await Create("me", client.Id, Doc("Quote"));
        var order = await Create("me", client.Id, Doc("Order", predecessor: quote.Id));
        await As("me").Upload(quote.Id, File("offerte.pdf"), null, CancellationToken.None);
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));

        Assert.IsType<NoContentResult>(await As("me").Delete(quote.Id));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        _db.ChangeTracker.Clear();
        Assert.Null((await _db.ClientDocuments.SingleAsync(d => d.Id == order.Id)).PredecessorId);
    }

    [Fact]
    public async Task DeletingTheClient_RemovesItsFilesFromDisk()
    {
        var client = SeedClient();
        var doc = await Create("me", client.Id, Doc());
        await As("me").Upload(doc.Id, File("a.pdf"), null, CancellationToken.None);
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));

        var clients = new ClientsController(_db, _storage)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "me") }, "Test")),
                },
            },
        };
        Assert.IsType<NoContentResult>(await clients.DeleteClient(client.Id));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Empty(await _db.ClientDocuments.ToListAsync());
    }

    [Theory]
    [InlineData("a.PDF", "application/pdf")]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public void ContentType_FollowsTheExtension(string name, string expected) =>
        Assert.Equal(expected, DocumentStorage.ContentTypeFor(name));

    [Theory]
    [InlineData("a.exe")]
    [InlineData("a.html")]
    [InlineData("a.svg")]
    [InlineData("noextension")]
    public void ContentType_RefusesRiskyTypes(string name) => Assert.Null(DocumentStorage.ContentTypeFor(name));

    [Fact]
    public void DownloadAndDeletes_StayClosedToApiKeys()
    {
        foreach (var action in new[] { nameof(DocumentsController.Download), nameof(DocumentsController.DeleteFile), nameof(DocumentsController.Delete) })
            Assert.NotNull(typeof(DocumentsController).GetMethod(action)!.GetCustomAttributes(typeof(DenyApiKeyAttribute), false).FirstOrDefault());
    }

    [Fact]
    public void CleanName_DropsBidiOverridesAndPaths()
    {
        Assert.Equal("reportfdp.exe", DocumentStorage.CleanName("report\u202Efdp.exe"));
        Assert.Equal("c.pdf", DocumentStorage.CleanName("a/b\\c.pdf"));
        Assert.Equal("document", DocumentStorage.CleanName("   "));
    }

    [Fact]
    public async Task RootWithTrailingSlash_StillWorks()
    {
        var storage = new DocumentStorage(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Documents:Path"] = _root + Path.DirectorySeparatorChar }).Build());
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var (path, size, _) = await storage.SaveAsync(content, CancellationToken.None);
        Assert.Equal(1, size);
        using var stream = storage.Open(path);
        Assert.NotNull(stream);
    }
}
