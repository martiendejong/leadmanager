using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Fedha;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>The read-only Fedha invoice sync: status mapping, idempotence, client matching and unmatched contacts.</summary>
public class FedhaSyncTests : IDisposable
{
    private const string Owner = "owner-1";
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private FakeFedha _fedha = new();

    public FedhaSyncTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class FakeFedha : IFedhaClient
    {
        public List<FedhaInvoiceRow> Rows { get; } = new();
        public Task<IReadOnlyList<FedhaInvoiceRow>> GetInvoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FedhaInvoiceRow>>(Rows);
    }

    private static FedhaInvoiceRow Row(string number, int contact, string contactName, int status, decimal sub = 100m, decimal open = 121m,
        string? desc = "Work", DateTime? sent = null, DateTime? paid = null, DateTime? date = null, int overdue = 0) =>
        new(0, contact, contactName, number,
            date ?? new DateTime(2026, 11, 5), status, desc, sub, Math.Round(sub * 0.21m, 2), Math.Round(sub * 1.21m, 2), open, paid, sent, overdue);

    private Client AddClient(string name)
    {
        var c = new Client { Name = name, CreatedByUserId = Owner };
        _db.Clients.Add(c);
        _db.SaveChanges();
        return c;
    }

    private FedhaSyncService Svc() => new(_db, _fedha);

    [Fact]
    public async Task Draft_is_never_reported_as_sent()
    {
        var c = AddClient("Weerzien Mara");
        _fedha.Rows.Add(Row("2026-30", 17, "Weerzien Mara", 1));
        var r = await Svc().RunAsync(Owner, "test");
        Assert.Equal(1, r.Created);
        var inv = _db.ClientInvoices.Single(i => i.ClientId == c.Id);
        Assert.Equal(InvoiceStatus.Draft, inv.Status);
        Assert.False(inv.SendUnknown);
        Assert.Null(inv.OutstandingAmount);
    }

    [Fact]
    public async Task Sent_without_send_date_is_flagged_send_unknown_and_open()
    {
        var c = AddClient("Persch Art");
        _fedha.Rows.Add(Row("202615", 12, "Persch Art International", 2, 6775m, 8197.75m));
        await Svc().RunAsync(Owner, "test");
        var inv = _db.ClientInvoices.Single(i => i.ClientId == c.Id);
        Assert.Equal(InvoiceStatus.Sent, inv.Status);
        Assert.True(inv.SendUnknown);
        Assert.Equal(8197.75m, inv.OutstandingAmount);
        Assert.Equal("fedha:202615", inv.SourceRef);
    }

    [Fact]
    public async Task Sent_with_send_date_is_proven_and_overdue_stays_sent()
    {
        var c = AddClient("Persch Art");
        var sent = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        _fedha.Rows.Add(Row("202612", 12, "Persch Art International", 4, 350m, 423.5m, sent: sent, overdue: 12));
        await Svc().RunAsync(Owner, "test");
        var inv = _db.ClientInvoices.Single(i => i.ClientId == c.Id);
        Assert.Equal(InvoiceStatus.Sent, inv.Status);
        Assert.False(inv.SendUnknown);
        Assert.Equal(sent, inv.SentAt);
        Assert.Contains("Overdue", inv.Notes);
    }

    [Fact]
    public async Task Paid_and_cancelled_and_rounding_residue_are_mapped()
    {
        var c = AddClient("Port of Giethoorn");
        _fedha.Rows.Add(Row("202614", 5, "Giethoorn Hospitality", 3, 1090m, 0m, paid: new DateTime(2026, 8, 20)));
        _fedha.Rows.Add(Row("INV-2026-0002", 5, "Giethoorn Hospitality", 5, 413m, -0.0038m));
        _fedha.Rows.Add(Row("INV-2026-0007", 5, "Giethoorn Hospitality", 4, 41.32m, -0.0028m));
        await Svc().RunAsync(Owner, "test");
        var inv = _db.ClientInvoices.Where(i => i.ClientId == c.Id).ToDictionary(i => i.Number);
        Assert.Equal(InvoiceStatus.Paid, inv["202614"].Status);
        Assert.Equal(new DateTime(2026, 8, 20), inv["202614"].PaidAt?.Date);
        Assert.Equal(InvoiceStatus.Cancelled, inv["INV-2026-0002"].Status);
        Assert.Equal(InvoiceStatus.Paid, inv["INV-2026-0007"].Status);
    }

    [Fact]
    public async Task Rerun_creates_no_duplicates_and_reports_unchanged()
    {
        AddClient("Persch Art");
        _fedha.Rows.Add(Row("202613", 12, "Persch Art International", 3, 5000m, 0m, paid: new DateTime(2026, 10, 30)));
        _fedha.Rows.Add(Row("2026-19", 12, "Persch Art International", 1, 200m, 242m));
        await Svc().RunAsync(Owner, "test");
        var again = await Svc().RunAsync(Owner, "test");
        Assert.Equal(0, again.Created);
        Assert.Equal(0, again.Updated);
        Assert.Equal(2, again.Unchanged);
        Assert.Equal(2, _db.ClientInvoices.Count());
    }

    [Fact]
    public async Task Existing_invoice_from_the_old_loader_is_updated_not_duplicated_and_keeps_its_service_link()
    {
        var c = AddClient("Autorijschool De Luxe");
        var svc = new ClientService { ClientId = c.Id, Name = "Hosting", Status = ServiceStatus.Active, BillingInterval = BillingInterval.OneOff };
        _db.ClientServices.Add(svc);
        _db.ClientInvoices.Add(new ClientInvoice { ClientId = c.Id, ServiceId = svc.Id, Number = "202618", SourceRef = "fedha:202618", Status = InvoiceStatus.Sent, AmountExclVat = 500m });
        _db.SaveChanges();
        _fedha.Rows.Add(Row("202618", 15, "Autorijschool De Luxe", 3, 500m, 0m, paid: new DateTime(2026, 11, 20)));
        var r = await Svc().RunAsync(Owner, "test");
        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.Updated);
        var inv = _db.ClientInvoices.Single();
        Assert.Equal(InvoiceStatus.Paid, inv.Status);
        Assert.Equal(svc.Id, inv.ServiceId);
    }

    [Fact]
    public async Task Contacts_without_a_client_are_listed_and_no_client_is_created()
    {
        AddClient("Persch Art");
        _fedha.Rows.Add(Row("202620", 99, "Joe Dijkhuis", 2, 600m, 726m));
        _fedha.Rows.Add(Row("202621", 99, "Joe Dijkhuis", 3, 100m, 0m));
        var before = _db.Clients.Count();
        var r = await Svc().RunAsync(Owner, "test");
        Assert.Equal(before, _db.Clients.Count());
        var u = Assert.Single(r.Unmatched);
        Assert.Equal("Joe Dijkhuis", u.Name);
        Assert.Equal(2, u.InvoiceCount);
        Assert.Equal(726m, u.OpenAmount);
        Assert.Empty(_db.ClientInvoices);
    }

    [Fact]
    public async Task A_manual_link_makes_an_unmatched_contact_match_on_the_next_run()
    {
        var c = AddClient("Social Media Hulp B.V.");
        _fedha.Rows.Add(Row("202611", 7, "Hr SR Schepman", 3, 5500m, 0m));
        Assert.Single((await Svc().RunAsync(Owner, "test")).Unmatched);
        _db.FedhaContactLinks.Add(new FedhaContactLink { UserId = Owner, FedhaContactId = 7, FedhaContactName = "Hr SR Schepman", ClientId = c.Id });
        _db.SaveChanges();
        var r = await Svc().RunAsync(Owner, "test");
        Assert.Empty(r.Unmatched);
        Assert.Equal(c.Id, _db.ClientInvoices.Single().ClientId);
    }

    [Fact]
    public async Task Other_users_clients_are_not_touched()
    {
        _db.Clients.Add(new Client { Name = "Weerzien Mara", CreatedByUserId = "someone-else" });
        _db.SaveChanges();
        _fedha.Rows.Add(Row("2026-30", 17, "Weerzien Mara", 1));
        var r = await Svc().RunAsync(Owner, "test");
        Assert.Single(r.Unmatched);
        Assert.Empty(_db.ClientInvoices);
    }

    [Fact]
    public async Task Monthly_hosting_invoice_is_linked_to_its_service_with_the_period()
    {
        var c = AddClient("Weerzien Mara");
        var svc = new ClientService { ClientId = c.Id, Name = "Hosting en beheer weerzienmara.nl", Status = ServiceStatus.Active, BillingInterval = BillingInterval.Monthly, BillingAmount = 45m, StartDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) };
        _db.ClientServices.Add(svc);
        _db.SaveChanges();
        _fedha.Rows.Add(Row("2026-30", 17, "Weerzien Mara", 2, 45m, 54.45m, desc: "Hosting en beheer weerzienmara.nl · november 2026", sent: new DateTime(2026, 12, 5)));
        await Svc().RunAsync(Owner, "test");
        var inv = _db.ClientInvoices.Single();
        Assert.Equal(svc.Id, inv.ServiceId);
        Assert.Equal(new DateTime(2026, 11, 1), inv.PeriodStart?.Date);
        Assert.Equal(new DateTime(2026, 11, 30), inv.PeriodEnd?.Date);
    }

    [Fact]
    public void Month_period_is_read_from_dutch_descriptions_and_ignored_otherwise()
    {
        Assert.Equal((new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)), FedhaSyncService.ParseMonthPeriod("Hosting februari 2026"));
        Assert.Null(FedhaSyncService.ParseMonthPeriod("Hosting augustus - november 2026"));
        Assert.Null(FedhaSyncService.ParseMonthPeriod(null));
    }

    [Fact]
    public void Daily_job_waits_until_0530_utc()
    {
        Assert.Equal(TimeSpan.FromHours(1), FedhaSyncBackgroundService.DelayUntilNext(new DateTime(2026, 11, 25, 4, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(23), FedhaSyncBackgroundService.DelayUntilNext(new DateTime(2026, 11, 25, 6, 30, 0, DateTimeKind.Utc)));
    }
}
