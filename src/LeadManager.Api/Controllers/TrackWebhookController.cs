using System.Text;
using System.Text.Json;
using JengoTrack;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Track;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeadManager.Api.Controllers;

/// <summary>
/// Receives "link.clicked" webhooks from Jengo Track (task 8071). Anonymous: the HMAC signature is the
/// authentication. A human click becomes an Interaction, and enough human clicks plan a "Bel nu" action.
/// </summary>
[ApiController]
[AllowAnonymous]
public class TrackWebhookController : ControllerBase
{
    public const int MaxBodyBytes = 64 * 1024;
    public const string CallNowPrefix = "Call now:";
    /// <summary>Open actions created before the interface became English start with this; they still count as open.</summary>
    private const string LegacyCallNowPrefix = "Bel nu:";

    private readonly LeadManagerDbContext _db;
    private readonly TrackOptions _options;
    private readonly ILogger<TrackWebhookController> _log;

    public TrackWebhookController(LeadManagerDbContext db, IOptions<TrackOptions> options, ILogger<TrackWebhookController> log)
    {
        _db = db;
        _options = options.Value;
        _log = log;
    }

    // POST /api/integrations/track/webhook
    [HttpPost("api/integrations/track/webhook")]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.WebhookSecret))
            return StatusCode(503, "Track integration is not enabled");

        // The raw body is read once: the signature covers the exact bytes Track sent.
        string raw;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, false, 1024, leaveOpen: true))
        {
            var buffer = new char[MaxBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (read > MaxBodyBytes) return StatusCode(413);
            raw = new string(buffer, 0, read);
        }

        Request.Headers.TryGetValue("X-Track-Signature", out var signature);
        if (!JengoTrackClient.VerifySignature(_options.WebhookSecret, raw, signature.ToString()))
            return Unauthorized();

        ClickWebhook? click;
        try { click = JsonSerializer.Deserialize<ClickWebhook>(raw); }
        catch (JsonException) { return BadRequest("Invalid JSON"); }
        if (click == null) return BadRequest("Invalid JSON");

        if (click.Event != "link.clicked") return Ok(new { handled = false, reason = "ignored event" });

        var type = click.EntityType?.ToLowerInvariant();
        if ((type != "lead" && type != "client") || !Guid.TryParse(click.EntityId, out var id))
            return Ok(new { handled = false, reason = "not a leadmanager entity" });

        Guid? leadId = null, clientId = null;
        string? ownerId, name;
        if (type == "lead")
        {
            var lead = await _db.Leads.Where(l => l.Id == id).Select(l => new { l.Name, l.ImportedByUserId }).FirstOrDefaultAsync(ct);
            if (lead == null) return Ok(new { handled = false, reason = "unknown entity" });
            leadId = id; ownerId = lead.ImportedByUserId; name = lead.Name;
        }
        else
        {
            var client = await _db.Clients.Where(c => c.Id == id).Select(c => new { c.Name, c.CreatedByUserId }).FirstOrDefaultAsync(ct);
            if (client == null) return Ok(new { handled = false, reason = "unknown entity" });
            clientId = id; ownerId = client.CreatedByUserId; name = client.Name;
        }

        // Idempotent: Track retries a delivery with the same token and ts.
        var sourceRef = Cut($"track:{click.Token}:{click.Ts}", CrmRules.MaxSourceRef);
        var duplicate = await _db.Interactions.AnyAsync(i => i.LeadId == leadId && i.ClientId == clientId && i.SourceRef == sourceRef, ct);
        if (duplicate) return Ok(new { handled = true, duplicate = true });

        var what = CrmRules.Clean(click.Label) ?? CrmRules.Clean(click.Url) ?? "a link";
        var details = new List<string>();
        if (CrmRules.Clean(click.Channel) is { } channel) details.Add($"Channel: {channel}");
        if (CrmRules.Clean(click.Campaign) is { } campaign) details.Add($"Campaign: {campaign}");
        if (CrmRules.Clean(click.SourceTool) is { } source) details.Add($"Source: {source}");
        var summary = $"Clicked {what}" + (details.Count > 0 ? "\n" + string.Join(" · ", details) : "");

        _db.Interactions.Add(new Interaction
        {
            LeadId = leadId,
            ClientId = clientId,
            Kind = InteractionKind.Note,
            Direction = InteractionDirection.Inbound,
            OccurredAt = click.Ts > 0 ? click.At.UtcDateTime : DateTime.UtcNow,
            Summary = Cut(summary, CrmRules.MaxSummary),
            Outcome = "Click",
            SourceRef = sourceRef,
        });

        var hot = (click.TotalHumanClicks ?? 0) >= Math.Max(1, _options.HotThreshold);
        if (hot)
        {
            var hasOpen = await _db.NextActions.AnyAsync(a =>
                a.LeadId == leadId && a.ClientId == clientId && a.DoneAt == null && (a.Title.StartsWith(CallNowPrefix) || a.Title.StartsWith(LegacyCallNowPrefix)), ct);
            if (!hasOpen)
            {
                _db.NextActions.Add(new NextAction
                {
                    LeadId = leadId,
                    ClientId = clientId,
                    Title = Cut($"{CallNowPrefix} {name} read your message", CrmRules.MaxTitle),
                    // End of today (Dutch time): on the Today list at once, not overdue at once.
                    DueAt = NextActionsController.EndOfDayNl(DateTime.UtcNow),
                    AssignedToUserId = ownerId,
                });
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A parallel delivery of the same click won the unique (parent, SourceRef) index.
            _log.LogInformation("Track webhook: duplicate click {Ref} lost the race", sourceRef);
            return Ok(new { handled = true, duplicate = true });
        }
        return Ok(new { handled = true });
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
