using System.Text.Json.Serialization;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

public record DashboardCounts(
    int Leads, int Clients, int ActiveProjects, int OpenActions, int DueToday, int Overdue,
    int OpenQuotes, decimal OpenQuotesAmount, int AwaitingSignature);

public record DashboardClientRow(
    Guid Id, string Name, double Score, string? EngagementCategory,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? NextActionAt,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? LastContactAt);

public record DashboardDocumentRow(
    Guid Id, Guid ClientId, string ClientName, string Kind, string Status, string Title, decimal? Amount,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime DocumentDate,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? ValidUntil);

public record DashboardActivityRow(
    Guid Id, string ParentType, Guid ParentId, string ParentName, string Kind, string Summary,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime At);

public record DashboardDto(
    DashboardCounts Counts,
    List<DashboardClientRow> TopClients,
    List<DashboardClientRow> NoNextAction,
    List<DashboardClientRow> Silent,
    List<DashboardDocumentRow> AwaitingSignature,
    List<DashboardDocumentRow> Expiring,
    Dictionary<string, int> ProjectsByStatus,
    Dictionary<string, int> LeadsByStage,
    List<DashboardActivityRow> RecentActivity);

/// <summary>Everything the front page needs in one request. All figures are about the caller's own data.</summary>
[ApiController]
[Authorize]
public class DashboardController : ControllerBase
{
    private const int SilentDays = 30;
    private const int ExpiringDays = 45;
    private const int RowLimit = 6;

    // Only documents that someone has to sign wait for a signature (not invoices or loose files).
    private static readonly DocumentKind[] Signable = { DocumentKind.Quote, DocumentKind.Order, DocumentKind.Agreement, DocumentKind.Dpa, DocumentKind.Nda };
    private static readonly string[] RunningProjectStatuses = { "Pilot", "Active", "Maintenance" };

    private readonly LeadManagerDbContext _db;

    public DashboardController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/dashboard
    [HttpGet("api/dashboard")]
    public Task<IActionResult> Get() => BuildAsync(DateTime.UtcNow);

    /// <summary>The reference time is not a request parameter: a caller must not be able to move "now".</summary>
    [NonAction]
    public async Task<IActionResult> BuildAsync(DateTime utcNow)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        utcNow = UtcDateTimeConverter.Normalize(utcNow);
        var endOfToday = NextActionsController.EndOfDayNl(utcNow);
        var startOfToday = endOfToday.AddDays(-1); // same day boundary as the Today list: Dutch midnight

        var clients = await _db.Clients.AsNoTracking().Where(c => c.CreatedByUserId == userId).ToListAsync();
        var clientIds = clients.Select(c => c.Id).ToList();

        var openActions = await _db.NextActions.AsNoTracking()
            .Where(a => a.AssignedToUserId == userId && a.DoneAt == null).ToListAsync();
        var nextByClient = openActions.Where(a => a.ClientId.HasValue)
            .GroupBy(a => a.ClientId!.Value).ToDictionary(g => g.Key, g => g.Min(a => a.DueAt));

        var lastContact = (await _db.Interactions.AsNoTracking()
                .Where(i => i.ClientId != null && clientIds.Contains(i.ClientId.Value))
                .GroupBy(i => i.ClientId!.Value)
                .Select(g => new { g.Key, At = g.Max(i => i.OccurredAt) })
                .ToListAsync())
            .ToDictionary(x => x.Key, x => x.At);

        DashboardClientRow Row(Client c) => new(
            c.Id, c.Name, ClientPriority.Score(c.PriorityValue, c.PriorityUrgency, c.PriorityEffort), c.EngagementCategory,
            nextByClient.TryGetValue(c.Id, out var n) ? UtcDateTimeConverter.Normalize(n) : null,
            lastContact.TryGetValue(c.Id, out var l) ? UtcDateTimeConverter.Normalize(l) : null);

        var rows = clients.Select(Row).ToList();
        // Reactive clients are paid-requests-only: they are never "needs attention".
        var attentionPool = rows.Where(r => r.EngagementCategory != "Reactive").ToList();

        var documents = await _db.ClientDocuments.AsNoTracking()
            .Where(d => clientIds.Contains(d.ClientId)).ToListAsync();
        var clientNames = clients.ToDictionary(c => c.Id, c => c.Name);
        DashboardDocumentRow Doc(ClientDocument d) => new(
            d.Id, d.ClientId, clientNames[d.ClientId], d.Kind.ToString(), d.Status.ToString(), d.Title, d.Amount, d.DocumentDate, d.ValidUntil);

        var openQuotes = documents.Where(d => d.Kind == DocumentKind.Quote && d.Status == DocumentStatus.Sent).ToList();
        var awaiting = documents.Where(d => d.Status == DocumentStatus.Sent && Signable.Contains(d.Kind)).OrderBy(d => d.DocumentDate).ToList();
        var expiring = documents
            .Where(d => d.ValidUntil.HasValue && d.ValidUntil >= utcNow && d.ValidUntil <= utcNow.AddDays(ExpiringDays)
                        && d.Status is DocumentStatus.Sent or DocumentStatus.Signed)
            .OrderBy(d => d.ValidUntil).ToList();

        var projects = await _db.Projects.AsNoTracking().Where(p => clientIds.Contains(p.ClientId)).ToListAsync();

        var leads = await _db.Leads.AsNoTracking().Where(l => l.ImportedByUserId == userId)
            .GroupBy(l => l.PipelineStatus).Select(g => new { g.Key, N = g.Count() }).ToListAsync();

        var leadNames = await _db.Leads.AsNoTracking().Where(l => l.ImportedByUserId == userId).ToDictionaryAsync(l => l.Id, l => l.Name);
        var ownLeadIds = _db.Leads.Where(l => l.ImportedByUserId == userId).Select(l => l.Id);
        var recent = await _db.Interactions.AsNoTracking()
            .Where(i => (i.ClientId != null && clientIds.Contains(i.ClientId.Value)) || (i.LeadId != null && ownLeadIds.Contains(i.LeadId.Value)))
            .OrderByDescending(i => i.OccurredAt).Take(8).ToListAsync();

        var counts = new DashboardCounts(
            Leads: leads.Sum(x => x.N),
            Clients: clients.Count,
            ActiveProjects: projects.Count(p => RunningProjectStatuses.Contains(p.Status)),
            OpenActions: openActions.Count,
            // Due today = anywhere in today (Dutch time); overdue = before today. Together they are what the Today list shows.
            DueToday: openActions.Count(a => a.DueAt >= startOfToday && a.DueAt <= endOfToday),
            Overdue: openActions.Count(a => a.DueAt < startOfToday),
            OpenQuotes: openQuotes.Count,
            OpenQuotesAmount: openQuotes.Sum(d => d.Amount ?? 0),
            AwaitingSignature: awaiting.Count);

        return Ok(new DashboardDto(
            counts,
            TopClients: rows.Where(r => r.Score > 0).OrderByDescending(r => r.Score).ThenBy(r => r.Name).Take(RowLimit).ToList(),
            NoNextAction: attentionPool.Where(r => r.NextActionAt == null).OrderByDescending(r => r.Score).ThenBy(r => r.Name).Take(RowLimit).ToList(),
            Silent: attentionPool.Where(r => r.LastContactAt == null || r.LastContactAt < utcNow.AddDays(-SilentDays))
                .OrderByDescending(r => r.Score).ThenBy(r => r.LastContactAt ?? DateTime.MinValue).Take(RowLimit).ToList(),
            AwaitingSignature: awaiting.Take(RowLimit).Select(Doc).ToList(),
            Expiring: expiring.Take(RowLimit).Select(Doc).ToList(),
            ProjectsByStatus: projects.Where(p => p.Status != "Archived").GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
            LeadsByStage: leads.ToDictionary(x => x.Key.ToString(), x => x.N),
            RecentActivity: recent.Select(i => new DashboardActivityRow(
                i.Id, i.ClientId.HasValue ? "clients" : "leads", (i.ClientId ?? i.LeadId)!.Value,
                i.ClientId.HasValue ? clientNames.GetValueOrDefault(i.ClientId.Value, "") : leadNames.GetValueOrDefault(i.LeadId!.Value, ""),
                i.Kind.ToString(), i.Summary.Length > 160 ? i.Summary[..160] + "…" : i.Summary, i.OccurredAt)).ToList()));
    }
}
