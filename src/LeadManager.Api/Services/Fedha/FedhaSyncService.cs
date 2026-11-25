using System.Globalization;
using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Fedha;

public record UnmatchedContact(int FedhaContactId, string Name, int InvoiceCount, decimal OpenAmount, List<string> Numbers);
public record FedhaSyncResult(Guid RunId, int Created, int Updated, int Unchanged, int Skipped, List<UnmatchedContact> Unmatched);

/// <summary>
/// Copies Fedha invoices into the clients' invoice lists so /agreements can show what was sent, what is open and what is paid.
/// Read-only towards Fedha. Idempotent: an invoice is found again by SourceRef fedha:&lt;number&gt; (or its number), so reruns and
/// the earlier one-off loader do not create duplicates. Never creates clients; contacts without a client are reported instead.
/// </summary>
public class FedhaSyncService
{
    /// <summary>Fedha contact names that differ from the client name. Manual links (FedhaContactLink) win over these.</summary>
    public static readonly Dictionary<string, string> DefaultAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Persch Art International"] = "Persch Art",
        ["Giethoorn Hospitality"] = "Port of Giethoorn",
    };

    private static readonly string[] DutchMonths =
        { "januari", "februari", "maart", "april", "mei", "juni", "juli", "augustus", "september", "oktober", "november", "december" };

    private readonly LeadManagerDbContext _db;
    private readonly IFedhaClient _fedha;

    public FedhaSyncService(LeadManagerDbContext db, IFedhaClient fedha) { _db = db; _fedha = fedha; }

    /// <summary>Maps a Fedha status to the Clients status. A concept is never reported as sent.</summary>
    public static (InvoiceStatus Status, bool SendUnknown, string Note) MapStatus(FedhaInvoiceRow f)
    {
        var open = Math.Round(f.OutstandingAmount, 2);
        var sentOn = f.LastSentAt.HasValue ? $"sent per Fedha {f.LastSentAt:yyyy-MM-dd}" : "send date not recorded in Fedha";
        switch (f.Status)
        {
            case 1:
                return (InvoiceStatus.Draft, false, "Concept in Fedha, not sent");
            case 5:
                return (InvoiceStatus.Cancelled, false, "Cancelled per Fedha");
            case 3:
                return (InvoiceStatus.Paid, !f.LastSentAt.HasValue, "Paid per Fedha" + (f.PaidDate.HasValue ? $" on {f.PaidDate:yyyy-MM-dd}" : "") + (f.LastSentAt.HasValue ? "" : "; " + sentOn));
            case 2:
            case 4:
                if (open < 0.01m && f.Total > 0.01m)
                    return (InvoiceStatus.Paid, !f.LastSentAt.HasValue, "Paid per Fedha (nothing outstanding)" + (f.LastSentAt.HasValue ? "" : "; " + sentOn));
                var overdue = f.Status == 4 ? $"Overdue per Fedha ({f.DaysOverdue} days); " : "";
                return (InvoiceStatus.Sent, !f.LastSentAt.HasValue, $"{overdue}open per Fedha (outstanding {open:0.00}); {sentOn}");
            default:
                return (InvoiceStatus.Draft, false, $"Unknown Fedha status {f.Status}; treated as concept");
        }
    }

    /// <summary>Reads "{month name} {year}" (Dutch) from an invoice description and returns the first and last day of that month.</summary>
    public static (DateTime Start, DateTime End)? ParseMonthPeriod(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var d = description.ToLowerInvariant();
        if (DutchMonths.Count(mn => d.Contains(mn, StringComparison.Ordinal)) != 1) return null; // a range such as "augustus - november 2026" is not one period
        for (var m = 0; m < 12; m++)
        {
            var idx = d.IndexOf(DutchMonths[m], StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = d[(idx + DutchMonths[m].Length)..].TrimStart();
            if (rest.Length >= 4 && int.TryParse(rest[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 2000 and <= 2100)
            {
                var start = new DateTime(year, m + 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return (start, start.AddMonths(1).AddDays(-1));
            }
        }
        return null;
    }

    public async Task<FedhaSyncResult> RunAsync(string userId, string trigger, CancellationToken ct = default)
    {
        var run = new FedhaSyncRun { UserId = userId, Trigger = trigger };
        _db.FedhaSyncRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        try
        {
            var rows = await _fedha.GetInvoicesAsync(ct);
            var result = await ApplyAsync(userId, rows, run, ct);
            run.FinishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return result;
        }
        catch (Exception e)
        {
            run.Error = e.Message.Length > 500 ? e.Message[..500] : e.Message;
            run.FinishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<FedhaSyncResult> ApplyAsync(string userId, IReadOnlyList<FedhaInvoiceRow> rows, FedhaSyncRun run, CancellationToken ct = default)
    {
        var clients = await _db.Clients.Where(c => c.CreatedByUserId == userId && c.IsActive).ToListAsync(ct);
        var byName = clients.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var links = await _db.FedhaContactLinks.Where(l => l.UserId == userId).ToListAsync(ct);
        var clientIds = clients.Select(c => c.Id).ToList();
        var services = await _db.ClientServices.Where(s => clientIds.Contains(s.ClientId)).ToListAsync(ct);
        var invoices = await _db.ClientInvoices.Where(i => clientIds.Contains(i.ClientId)).ToListAsync(ct);
        var now = DateTime.UtcNow;

        Client? Resolve(FedhaInvoiceRow f)
        {
            var link = links.FirstOrDefault(l => l.FedhaContactId == f.ContactId);
            if (link != null) return clients.FirstOrDefault(c => c.Id == link.ClientId);
            var name = (f.ContactName ?? "").Trim();
            if (byName.TryGetValue(name, out var exact)) return exact;
            if (DefaultAliases.TryGetValue(name, out var alias) && byName.TryGetValue(alias, out var viaAlias)) return viaAlias;
            return null;
        }

        int created = 0, updated = 0, unchanged = 0, skipped = 0;
        var unmatched = new Dictionary<int, UnmatchedContact>();

        foreach (var f in rows.OrderBy(r => r.InvoiceDate))
        {
            if (string.IsNullOrWhiteSpace(f.InvoiceNumber)) { skipped++; continue; }
            var client = Resolve(f);
            if (client == null)
            {
                var open = f.Status is 2 or 4 ? Math.Max(0, Math.Round(f.OutstandingAmount, 2)) : 0m;
                unmatched.TryGetValue(f.ContactId, out var u);
                unmatched[f.ContactId] = u == null
                    ? new UnmatchedContact(f.ContactId, f.ContactName ?? "", 1, open, new List<string> { f.InvoiceNumber })
                    : u with { InvoiceCount = u.InvoiceCount + 1, OpenAmount = u.OpenAmount + open, Numbers = u.Numbers.Append(f.InvoiceNumber).ToList() };
                continue;
            }

            var number = f.InvoiceNumber.Trim();
            var sref = "fedha:" + number;
            var inv = invoices.FirstOrDefault(i => i.ClientId == client.Id && (i.SourceRef == sref || i.Number == number));
            var isNew = inv == null;
            inv ??= new ClientInvoice { ClientId = client.Id, CreatedByUserId = userId };

            var (status, sendUnknown, note) = MapStatus(f);
            var vat = f.Subtotal != 0 ? Math.Round(f.VatAmount / f.Subtotal * 100m, 2) : 21m;
            var notes = ((f.Description ?? "").Trim() + " | " + note).Trim(' ', '|');
            decimal? outstanding = status == InvoiceStatus.Sent ? Math.Max(0, Math.Round(f.OutstandingAmount, 2))
                : status == InvoiceStatus.Paid ? 0m : null;
            var invoiceDate = DateTime.SpecifyKind(f.InvoiceDate.Date, DateTimeKind.Utc);
            DateTime? paidAt = status == InvoiceStatus.Paid && f.PaidDate.HasValue ? DateTime.SpecifyKind(f.PaidDate.Value.Date, DateTimeKind.Utc) : null;
            DateTime? sentAt = f.LastSentAt.HasValue ? DateTime.SpecifyKind(f.LastSentAt.Value, DateTimeKind.Utc) : null;

            var changed = isNew
                || inv.Number != number || inv.SourceRef != sref || inv.Status != status
                || inv.AmountExclVat != Math.Round(f.Subtotal, 2) || inv.VatRate != vat || inv.InvoiceDate != invoiceDate
                || inv.PaidAt != paidAt || inv.SentAt != sentAt || inv.SendUnknown != sendUnknown
                || inv.OutstandingAmount != outstanding || inv.Notes != notes;

            inv.Number = number;
            inv.SourceRef = sref;
            inv.Status = status;
            inv.AmountExclVat = Math.Round(f.Subtotal, 2);
            inv.VatRate = vat;
            inv.InvoiceDate = invoiceDate;
            inv.PaidAt = paidAt;
            inv.SentAt = sentAt;
            inv.SendUnknown = sendUnknown;
            inv.OutstandingAmount = outstanding;
            inv.Notes = notes;

            // Link to a service and period only when it is unambiguous, and never overwrite a link someone made by hand.
            if (inv.ServiceId == null)
            {
                var desc = (f.Description ?? "").ToLowerInvariant();
                var candidates = services.Where(s => s.ClientId == client.Id && s.Status == ServiceStatus.Active
                    && s.BillingInterval == BillingInterval.Monthly && !string.IsNullOrWhiteSpace(s.Name) && desc.Contains(s.Name.Trim().ToLowerInvariant())).ToList();
                var period = ParseMonthPeriod(f.Description);
                if (candidates.Count == 1 && period.HasValue)
                {
                    inv.ServiceId = candidates[0].Id;
                    inv.PeriodStart = period.Value.Start;
                    inv.PeriodEnd = period.Value.End;
                    changed = true;
                }
            }

            if (changed)
            {
                inv.SyncedAt = now;
                if (isNew) { _db.ClientInvoices.Add(inv); invoices.Add(inv); created++; } else updated++;
            }
            else unchanged++;
        }

        var list = unmatched.Values.OrderByDescending(u => u.OpenAmount).ThenBy(u => u.Name).ToList();
        run.Created = created; run.Updated = updated; run.Unchanged = unchanged; run.Unmatched = list.Count;
        run.UnmatchedJson = JsonSerializer.Serialize(list);
        await _db.SaveChangesAsync(ct);
        return new FedhaSyncResult(run.Id, created, updated, unchanged, skipped, list);
    }
}
