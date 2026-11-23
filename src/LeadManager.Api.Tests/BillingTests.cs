using System.Security.Claims;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>Billing terms per service, invoices tied to them, and the derived list of what is due.</summary>
public class BillingTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;

    public BillingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static ControllerContext Ctx(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
        },
    };

    private static ClientService Svc(decimal? amount = 45m, BillingInterval i = BillingInterval.Monthly, DateTime? start = null) => new()
    {
        ClientId = Guid.NewGuid(), Name = "Website management", Kind = ServiceKind.WebsiteManagement, Status = ServiceStatus.Active,
        BillingInterval = i, BillingAmount = amount, StartDate = start ?? D(2026, 11, 1),
    };

    private static ClientInvoice Inv(DateTime s, DateTime e, InvoiceStatus st = InvoiceStatus.Sent) =>
        new() { Number = "x", PeriodStart = s, PeriodEnd = e, Status = st, AmountExclVat = 45m };

    // ---- calculator ----

    [Fact]
    public void Monthly_service_without_invoices_is_due_from_its_start_date()
    {
        var d = BillingCalculator.Compute(Svc(), Array.Empty<ClientInvoice>(), Today);
        Assert.Equal("Due", d.State);
        Assert.Equal(D(2026, 11, 1), d.PeriodStart);
        Assert.Equal(D(2026, 11, 30), d.PeriodEnd);
        Assert.Equal(45m, d.AmountExclVat);
        Assert.Equal(54.45m, d.AmountInclVat);
    }

    [Fact]
    public void Next_period_follows_the_last_sent_invoice()
    {
        var d = BillingCalculator.Compute(Svc(start: D(2026, 9, 1)), new[] { Inv(D(2026, 9, 1), D(2026, 10, 31)) }, Today);
        Assert.Equal("Due", d.State);
        Assert.Equal(D(2026, 11, 1), d.PeriodStart);
    }

    [Fact]
    public void Draft_and_cancelled_invoices_do_not_cover_a_period()
    {
        var d = BillingCalculator.Compute(Svc(), new[] { Inv(D(2026, 11, 1), D(2026, 11, 30), InvoiceStatus.Draft), Inv(D(2026, 11, 1), D(2026, 11, 30), InvoiceStatus.Cancelled) }, Today);
        Assert.Equal(D(2026, 11, 1), d.PeriodStart);
    }

    [Fact]
    public void A_year_paid_ahead_is_covered_and_nothing_is_due_until_it_ends()
    {
        var s = Svc();
        s.PaidUntil = D(2027, 10, 31);
        var d = BillingCalculator.Compute(s, Array.Empty<ClientInvoice>(), Today);
        Assert.Equal("Covered", d.State);
        Assert.Equal(D(2027, 11, 1), d.PeriodStart);
        Assert.Equal(D(2027, 10, 31), d.CoveredThrough);
    }

    [Fact]
    public void A_free_year_is_flagged_and_billing_starts_after_it()
    {
        var s = Svc();
        s.FreeUntil = D(2027, 10, 31);
        s.FreeReason = "free year in the deal";
        var d = BillingCalculator.Compute(s, Array.Empty<ClientInvoice>(), Today);
        Assert.True(d.IsFree);
        Assert.Equal("Covered", d.State);
        Assert.Equal(D(2027, 11, 1), d.PeriodStart);
        Assert.Contains("free year in the deal", d.Explanation);
    }

    [Fact]
    public void Prepaid_period_ending_next_week_is_upcoming()
    {
        var s = Svc();
        s.PaidUntil = D(2026, 11, 30);
        var d = BillingCalculator.Compute(s, Array.Empty<ClientInvoice>(), Today);
        Assert.Equal("Upcoming", d.State);
        Assert.Equal(D(2026, 12, 1), d.PeriodStart);
    }

    [Fact]
    public void Yearly_and_quarterly_intervals_use_the_whole_period()
    {
        var y = BillingCalculator.Compute(Svc(540m, BillingInterval.Yearly), Array.Empty<ClientInvoice>(), Today);
        Assert.Equal(D(2027, 10, 31), y.PeriodEnd);
        var q = BillingCalculator.Compute(Svc(135m, BillingInterval.Quarterly), Array.Empty<ClientInvoice>(), Today);
        Assert.Equal(D(2027, 1, 31), q.PeriodEnd);
    }

    [Fact]
    public void Monthly_amount_is_the_fallback_and_scales_with_the_interval()
    {
        var s = Svc(null, BillingInterval.Quarterly);
        s.MonthlyAmount = 45m;
        Assert.Equal(135m, BillingCalculator.PeriodAmount(s));
    }

    [Fact]
    public void Several_open_periods_are_counted()
    {
        var d = BillingCalculator.Compute(Svc(start: D(2026, 8, 1)), Array.Empty<ClientInvoice>(), Today);
        Assert.Equal(4, d.PeriodsOverdue);
        Assert.Contains("4 periods are open", d.Explanation);
    }

    [Fact]
    public void The_last_period_is_prorated_to_the_end_date()
    {
        var s = Svc(300m);
        s.EndDate = D(2026, 11, 15);
        var d = BillingCalculator.Compute(s, Array.Empty<ClientInvoice>(), Today);
        Assert.Equal(D(2026, 11, 15), d.PeriodEnd);
        Assert.Equal(150m, d.AmountExclVat);
    }

    [Fact]
    public void Ended_inactive_hourly_and_incomplete_services_are_reported_without_a_guess()
    {
        var ended = Svc(); ended.EndDate = D(2026, 10, 31);
        Assert.Equal("Nothing", BillingCalculator.Compute(ended, new[] { Inv(D(2026, 11, 1), D(2026, 11, 30)) }, Today).State);
        var paused = Svc(); paused.Status = ServiceStatus.Paused;
        Assert.Equal("Nothing", BillingCalculator.Compute(paused, Array.Empty<ClientInvoice>(), Today).State);
        var hourly = Svc(null, BillingInterval.Hourly); hourly.HourlyRate = 75m;
        var h = BillingCalculator.Compute(hourly, Array.Empty<ClientInvoice>(), Today);
        Assert.Equal("NeedsInput", h.State);
        Assert.Contains("hours", h.Explanation);
        Assert.Equal("NeedsInput", BillingCalculator.Compute(Svc(null), Array.Empty<ClientInvoice>(), Today).State);
        var noStart = Svc(); noStart.StartDate = null;
        Assert.Equal("NeedsInput", BillingCalculator.Compute(noStart, Array.Empty<ClientInvoice>(), Today).State);
    }

    [Fact]
    public void One_off_is_due_once()
    {
        var s = Svc(1000m, BillingInterval.OneOff);
        Assert.Equal("Due", BillingCalculator.Compute(s, Array.Empty<ClientInvoice>(), Today).State);
        Assert.Equal("Nothing", BillingCalculator.Compute(s, new[] { Inv(D(2026, 11, 1), D(2026, 11, 1)) }, Today).State);
    }

    // ---- controllers ----

    private Client SeedClient(string owner = "me")
    {
        var c = new Client { Name = "Weerzien Mara", CreatedByUserId = owner };
        _db.Clients.Add(c);
        _db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task Billing_terms_roundtrip_and_old_clients_keep_them()
    {
        var c = SeedClient();
        var api = new ServicesController(_db) { ControllerContext = Ctx("me") };
        var created = Assert.IsType<CreatedResult>(await api.Create(c.Id, new UpsertServiceDto("WebsiteManagement", null, "Active", D(2026, 11, 1), null, null, null, null, "mail:1",
            "Monthly", 45m, null, null, null, D(2027, 10, 31), "free year in the deal", "Website management")));
        var dto = Assert.IsType<ServiceDto>(created.Value);
        Assert.Equal(45m, dto.BillingAmount);
        Assert.Equal(45m, dto.MonthlyAmount);
        Assert.Equal(D(2027, 10, 31), dto.FreeUntil);

        // An update that knows nothing about billing (an older client) does not wipe the terms.
        var upd = Assert.IsType<OkObjectResult>(await api.Update(dto.Id, new UpsertServiceDto("WebsiteManagement", "Website management", "Active", D(2026, 11, 1), null, 45m, "n", null)));
        var after = Assert.IsType<ServiceDto>(upd.Value);
        Assert.Equal(D(2027, 10, 31), after.FreeUntil);
        Assert.Equal("free year in the deal", after.FreeReason);

        Assert.IsType<BadRequestObjectResult>(await api.Update(dto.Id, new UpsertServiceDto("WebsiteManagement", null, "Active", null, null, null, null, null, null, "Weekly")));
    }

    [Fact]
    public async Task Invoices_are_idempotent_by_number_and_scoped_to_the_owner()
    {
        var c = SeedClient();
        var svc = new ClientService { ClientId = c.Id, Name = "Website management", Status = ServiceStatus.Active, BillingAmount = 45m, StartDate = D(2026, 11, 1) };
        _db.ClientServices.Add(svc);
        _db.SaveChanges();
        var api = new InvoicesController(_db) { ControllerContext = Ctx("me") };
        var dto = new UpsertInvoiceDto("2026-15", 45m, D(2026, 11, 1), D(2026, 11, 1), D(2026, 11, 30), null, "Sent", "anneke@example.com", null, null, svc.Id, null, "fedha:2026-15");
        Assert.IsType<CreatedResult>(await api.Create(c.Id, dto));
        Assert.IsType<OkObjectResult>(await api.Create(c.Id, dto));
        Assert.Equal(1, _db.ClientInvoices.Count());

        var due = Assert.IsType<OkObjectResult>(await api.Due(c.Id, null, Today));
        var items = Assert.IsType<List<ClientDueDto>>(due.Value).Single().Items;
        Assert.Equal(D(2026, 12, 1), items.Single().PeriodStart);
        Assert.Equal("Upcoming", items.Single().State);

        var other = new InvoicesController(_db) { ControllerContext = Ctx("someone-else") };
        Assert.IsType<NotFoundResult>(await other.List(c.Id));
        Assert.IsType<NotFoundResult>(await other.Due(c.Id, null, Today));

        Assert.IsType<BadRequestObjectResult>(await api.Create(c.Id, dto with { Number = "2026-16", PeriodEnd = D(2026, 10, 1), SourceRef = "x" }));
        Assert.IsType<BadRequestObjectResult>(await api.Create(c.Id, dto with { Number = "2026-17", ServiceId = Guid.NewGuid(), SourceRef = "y" }));
    }

    // ---- migration ----

    [Fact]
    public void Migration_keeps_existing_services_and_defaults_them_to_monthly()
    {
        var path = Path.Combine(Path.GetTempPath(), "lm-mig-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var cs = $"Data Source={path}";
            DbContextOptions<LeadManagerDbContext> Opt() => new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(cs).AddInterceptors(new TestOrgInterceptor()).Options;
            var clientId = Guid.NewGuid();
            using (var db = new LeadManagerDbContext(Opt()))
            {
                var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                migrator.Migrate("20261121190441_ClientServicesAndDocumentVersions");
                db.Database.ExecuteSqlRaw("INSERT INTO Clients (Id, Name, CreatedByUserId, IsActive, CreatedAt) VALUES ({0}, 'Old', 'me', 1, '2026-11-01')", clientId);
                db.Database.ExecuteSqlRaw("INSERT INTO ClientServices (Id, ClientId, Kind, Status, Name, MonthlyAmount, CreatedAt) VALUES ({0}, {1}, 'WebsiteManagement', 'Active', 'Old service', 45, '2026-11-01')", Guid.NewGuid(), clientId);
            }
            using (var db = new LeadManagerDbContext(Opt()))
            {
                db.Database.Migrate();
                var s = db.ClientServices.Single();
                Assert.Equal(BillingInterval.Monthly, s.BillingInterval);
                Assert.Equal(45m, s.MonthlyAmount);
                Assert.Null(s.BillingAmount);
                Assert.Equal(0, db.ClientInvoices.Count());
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
}
