using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Hunt;
using LeadManager.Api.Services.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>
/// Messages to leads: drafts, the history and the overview, the prefill from a template, bulk drafts and the "next to contact" queue.
/// THE APP NEVER SENDS ANYTHING. A draft is text. "Mark as sent" records that the user sent it themselves, needs an explicit confirmation, and is
/// the only thing that moves a lead from New to Contacted (it never moves a later status back). Everything is scoped to the lead's owner.
/// </summary>
[ApiController]
[Authorize]
[AllowApiKey]
[Route("api")]
public class MessagesController : ControllerBase
{
    public const int MaxBulk = 50;

    private readonly LeadManagerDbContext _db;

    public MessagesController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private static LeadMessageDto ToDto(LeadMessage m, string leadName, PipelineStatus? pipeline, int? score, Guid organizationId) =>
        new(m.Id, m.LeadId, leadName, m.Channel, m.Subject, m.Body, m.Status, m.CreatedAt, m.UpdatedAt, m.SentAt, pipeline?.ToString(), score, organizationId);

    private async Task<Lead?> OwnLeadAsync(Guid leadId) =>
        await _db.Leads.VisibleTo(_db, UserId).FirstOrDefaultAsync(l => l.Id == leadId);

    private async Task<(LeadMessage? Message, Lead? Lead)> OwnMessageAsync(Guid id)
    {
        var userId = UserId;
        var m = await _db.LeadMessages.FirstOrDefaultAsync(x => x.Id == id);
        if (m == null) return (null, null);
        var lead = await _db.Leads.VisibleTo(_db, userId).FirstOrDefaultAsync(l => l.Id == m.LeadId);
        return lead == null ? (null, null) : (m, lead);
    }

    // ---- the overview of all messages ------------------------------------------------------------------------------------

    // GET /api/messages?status=Draft|Sent&channel=Email&leadId=...&q=name&page=1&pageSize=50
    [HttpGet("messages")]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? channel, [FromQuery] Guid? leadId, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? organizationId = null)
    {
        var userId = UserId;
        if (status != null && status != MessageStatuses.Draft && status != MessageStatuses.Sent) return BadRequest(new { error = "Status is Draft or Sent." });
        var ch = channel == null ? null : MessageChannels.Canonical(channel);
        if (channel != null && ch == null) return BadRequest(new { error = $"Channel is one of: {string.Join(", ", MessageChannels.All)}." });
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = from m in _db.LeadMessages.AsNoTracking()
                    join l in _db.Leads.AsNoTracking() on m.LeadId equals l.Id
                    where _db.OrganizationMembers.Any(om => om.UserId == userId && om.OrganizationId == l.OrganizationId)
                    select new { m, l.Name, l.PipelineStatus, l.SalesPriorityScore, l.OrganizationId };
        if (status != null) query = query.Where(x => x.m.Status == status);
        if (ch != null) query = query.Where(x => x.m.Channel == ch);
        if (leadId.HasValue) query = query.Where(x => x.m.LeadId == leadId.Value);
        var orgs = await Organizations.ParseFilterAsync(_db, userId, organizationId);
        if (orgs != null) query = query.Where(x => orgs.Contains(x.OrganizationId));
        if (!string.IsNullOrWhiteSpace(q)) { var term = q.Trim(); query = query.Where(x => x.Name.Contains(term)); }

        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(x => x.m.UpdatedAt).ThenBy(x => x.m.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Ok(new { items = rows.Select(r => ToDto(r.m, r.Name, r.PipelineStatus, r.SalesPriorityScore, r.OrganizationId)), total, page, pageSize });
    }

    // GET /api/leads/{leadId}/messages
    [HttpGet("leads/{leadId:guid}/messages")]
    public async Task<IActionResult> ForLead(Guid leadId)
    {
        var lead = await OwnLeadAsync(leadId);
        if (lead == null) return NotFound();
        var items = await _db.LeadMessages.AsNoTracking().Where(m => m.LeadId == leadId)
            .OrderByDescending(m => m.UpdatedAt).ToListAsync();
        return Ok(items.Select(m => ToDto(m, lead.Name, lead.PipelineStatus, lead.SalesPriorityScore, lead.OrganizationId)));
    }

    // ---- the prefill ---------------------------------------------------------------------------------------------------------

    /// <summary>Subject and body from the templates: the user's template for the channel and product, then the user's default for the channel, then the built-in one.</summary>
    internal async Task<PrefillDto> BuildPrefillAsync(Lead lead, string channel, string? product)
    {
        var userId = UserId;
        // The sender is the signed in user's name (the company profile has no contact person).
        var me = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.FirstName, u.LastName }).FirstOrDefaultAsync();
        var sender = me == null ? "" : $"{me.FirstName} {me.LastName}".Trim();
        var values = MessageTemplates.ValuesFor(lead, product, sender);
        var productKey = ProductCatalog.Canonical(product) ?? (values.Product.Length > 0 ? ProductCatalog.All.FirstOrDefault(p => p.Name == values.Product)?.Key : null);

        var mine = await _db.MessageTemplates.AsNoTracking().Where(t => t.UserId == userId && t.Channel == channel).ToListAsync();
        var chosen = (productKey != null ? mine.FirstOrDefault(t => t.ProductKey == productKey) : null) ?? mine.FirstOrDefault(t => t.ProductKey == "");
        string subjectTemplate, bodyTemplate, source;
        if (chosen != null) { subjectTemplate = chosen.Subject; bodyTemplate = chosen.Body; source = chosen.ProductKey == "" ? "your default template" : $"your template for {chosen.ProductKey}"; }
        else { var b = MessageTemplates.Get(channel, values.Language); subjectTemplate = b.Subject; bodyTemplate = b.Body; source = "built-in template"; }

        var (subject, body) = MessageTemplates.Render(subjectTemplate, bodyTemplate, values);
        if (channel != MessageChannels.Email) subject = "";

        var warnings = new List<string>();
        var notes = await _db.LeadNotes.AsNoTracking().Where(n => n.LeadId == lead.Id).OrderByDescending(n => n.CreatedAt).Take(100).Select(n => n.Content).ToListAsync();
        var dnc = MessageRules.DoNotContactNote(notes);
        if (dnc != null) warnings.Add($"A note on this lead says not to contact it: \"{dnc}\". Check this before you send anything.");
        var address = MessageRules.AddressOf(lead);
        if (channel == MessageChannels.Email && address == null) warnings.Add("No valid e-mail address is published for this lead, so the mail app cannot be opened.");
        if (channel == MessageChannels.LinkedIn && string.IsNullOrWhiteSpace(lead.OwnerLinkedInUrl) && string.IsNullOrWhiteSpace(lead.LinkedInUrl)) warnings.Add("No LinkedIn profile is known for this lead.");
        if ((channel == MessageChannels.WhatsApp || channel == MessageChannels.Phone) && string.IsNullOrWhiteSpace(lead.OwnerMobile) && string.IsNullOrWhiteSpace(lead.Phone)) warnings.Add("No phone number is known for this lead.");
        if (lead.PipelineStatus != PipelineStatus.New) warnings.Add($"This lead is already {lead.PipelineStatus}.");

        return new PrefillDto(channel, subject, body, values.Language, productKey, source, address, warnings);
    }

    // GET /api/leads/{leadId}/messages/prefill?channel=Email&product=werkteam
    [HttpGet("leads/{leadId:guid}/messages/prefill")]
    public async Task<IActionResult> Prefill(Guid leadId, [FromQuery] string? channel, [FromQuery] string? product)
    {
        var lead = await OwnLeadAsync(leadId);
        if (lead == null) return NotFound();
        var ch = MessageChannels.Canonical(channel ?? MessageChannels.Email);
        if (ch == null) return BadRequest(new { error = $"Channel is one of: {string.Join(", ", MessageChannels.All)}." });
        if (!string.IsNullOrWhiteSpace(product) && ProductCatalog.Canonical(product) == null)
            return BadRequest(new { error = $"'{product}' is not one of our products. Known: {string.Join(", ", ProductCatalog.Keys)}." });
        return Ok(await BuildPrefillAsync(lead, ch, product));
    }

    // ---- drafts ------------------------------------------------------------------------------------------------------------------

    private IActionResult? Validate(string? channel, string? subject, string? body, out string ch, out string cleanSubject, out string cleanBody, bool bodyRequired)
    {
        ch = MessageChannels.Canonical(channel ?? MessageChannels.Email) ?? "";
        cleanSubject = MessageRules.SingleLine(subject, MessageRules.MaxSubject);
        cleanBody = MessageRules.Body(body);
        if (ch == "") return BadRequest(new { error = $"Channel is one of: {string.Join(", ", MessageChannels.All)}." });
        if ((subject ?? "").Length > MessageRules.MaxSubject * 2) return BadRequest(new { error = $"The subject is at most {MessageRules.MaxSubject} characters." });
        if ((body ?? "").Length > MessageRules.MaxBody * 2 || cleanBody.Length > MessageRules.MaxBody) return BadRequest(new { error = $"The message is at most {MessageRules.MaxBody} characters." });
        if (bodyRequired && cleanBody.Length == 0) return BadRequest(new { error = "The message cannot be empty." });
        if (ch != MessageChannels.Email) cleanSubject = "";
        return null;
    }

    // POST /api/leads/{leadId}/messages : a draft; it never changes the status of the lead
    [HttpPost("leads/{leadId:guid}/messages")]
    public async Task<IActionResult> CreateDraft(Guid leadId, [FromBody] CreateMessageDto dto)
    {
        var lead = await OwnLeadAsync(leadId);
        if (lead == null) return NotFound();
        var invalid = Validate(dto.Channel, dto.Subject, dto.Body, out var ch, out var subject, out var body, bodyRequired: true);
        if (invalid != null) return invalid;
        var m = new LeadMessage { LeadId = leadId, UserId = UserId!, Channel = ch, Subject = subject, Body = body };
        _db.LeadMessages.Add(m);
        await _db.SaveChangesAsync();
        return Created($"api/messages/{m.Id}", ToDto(m, lead.Name, lead.PipelineStatus, lead.SalesPriorityScore, lead.OrganizationId));
    }

    // PUT /api/messages/{id} : only a draft can be edited
    [HttpPut("messages/{id:guid}")]
    public async Task<IActionResult> UpdateDraft(Guid id, [FromBody] UpdateMessageDto dto)
    {
        var (m, lead) = await OwnMessageAsync(id);
        if (m == null || lead == null) return NotFound();
        if (m.Status != MessageStatuses.Draft) return Conflict(new { error = "A message that is marked as sent is history and cannot be edited." });
        var invalid = Validate(dto.Channel ?? m.Channel, dto.Subject ?? m.Subject, dto.Body ?? m.Body, out var ch, out var subject, out var body, bodyRequired: true);
        if (invalid != null) return invalid;
        m.Channel = ch; m.Subject = subject; m.Body = body; m.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(m, lead.Name, lead.PipelineStatus, lead.SalesPriorityScore, lead.OrganizationId));
    }

    // DELETE /api/messages/{id} : only a draft; the history of sent messages stays
    [HttpDelete("messages/{id:guid}")]
    public async Task<IActionResult> DeleteDraft(Guid id)
    {
        var (m, _) = await OwnMessageAsync(id);
        if (m == null) return NotFound();
        if (m.Status != MessageStatuses.Draft) return Conflict(new { error = "A message that is marked as sent is history and cannot be deleted." });
        _db.LeadMessages.Remove(m);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---- mark as sent ---------------------------------------------------------------------------------------------------------

    // POST /api/messages/{id}/sent  { "confirmed": true } : the user says they sent this message themselves
    [HttpPost("messages/{id:guid}/sent")]
    public async Task<IActionResult> MarkSent(Guid id, [FromBody] MarkSentDto dto)
    {
        var (m, lead) = await OwnMessageAsync(id);
        if (m == null || lead == null) return NotFound();
        if (dto.Confirmed != true) return BadRequest(new { error = "Confirm that you sent this message yourself (confirmed: true). The app never sends anything.", code = "confirmation_required" });
        if (m.Status == MessageStatuses.Sent) return Conflict(new { error = "This message is already marked as sent." });
        if (m.Body.Trim().Length == 0) return BadRequest(new { error = "The message is empty." });

        var now = DateTime.UtcNow;
        m.Status = MessageStatuses.Sent;
        m.SentAt = now;
        m.UpdatedAt = now;
        lead.LastContactedAt = now;

        // Only a lead that is still New becomes Contacted; a later status (Qualified, ProposalSent, Won, Lost) is never moved back.
        var changed = lead.PipelineStatus == PipelineStatus.New;
        if (changed) lead.PipelineStatus = PipelineStatus.Contacted;

        var text = m.Channel == MessageChannels.Email && m.Subject.Length > 0 ? $"\"{m.Subject}\" " : "";
        var excerpt = MessageRules.SingleLine(m.Body, 300);
        _db.Activities.Add(new LeadActivity
        {
            LeadId = lead.Id, UserId = UserId,
            ActivityType = m.Channel == MessageChannels.Email ? ActivityType.EmailSent : m.Channel == MessageChannels.Phone ? ActivityType.Called : ActivityType.NoteAdded,
            Note = $"Message sent by the user via {m.Channel}: {text}{excerpt}",
        });
        if (changed)
            _db.Activities.Add(new LeadActivity { LeadId = lead.Id, UserId = UserId, ActivityType = ActivityType.StatusChanged, Note = "Pipeline status New -> Contacted (a message was marked as sent)." });

        await _db.SaveChangesAsync();
        return Ok(new { message = ToDto(m, lead.Name, lead.PipelineStatus, lead.SalesPriorityScore, lead.OrganizationId), pipelineStatus = lead.PipelineStatus.ToString(), statusChanged = changed, lastContactedAt = lead.LastContactedAt });
    }

    // GET /api/messages/{id}/mailto : the mailto link, only for a valid published address
    [HttpGet("messages/{id:guid}/mailto")]
    public async Task<IActionResult> Mailto(Guid id)
    {
        var (m, lead) = await OwnMessageAsync(id);
        if (m == null || lead == null) return NotFound();
        if (m.Channel != MessageChannels.Email) return BadRequest(new { error = "Only an e-mail message has a mail link." });
        var address = MessageRules.AddressOf(lead);
        if (address == null) return BadRequest(new { error = "No valid e-mail address is published for this lead." });
        var url = MessageRules.Mailto(address, m.Subject, m.Body);
        if (url == null) return BadRequest(new { error = $"The message is too long for a mail link (about {MessageRules.MaxMailtoChars} characters). Copy it instead." });
        return Ok(new { url, address });
    }

    // ---- bulk drafts ------------------------------------------------------------------------------------------------------------

    // POST /api/messages/bulk-drafts : drafts for several leads at once. Drafts only: nothing is sent and no status changes.
    [HttpPost("messages/bulk-drafts")]
    public async Task<IActionResult> BulkDrafts([FromBody] BulkDraftsDto dto)
    {
        var userId = UserId;
        var ids = (dto.LeadIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        if (ids.Length == 0) return BadRequest(new { error = "No leads selected." });
        if (ids.Length > MaxBulk) return BadRequest(new { error = $"At most {MaxBulk} leads at a time." });
        var ch = MessageChannels.Canonical(dto.Channel ?? MessageChannels.Email);
        if (ch == null) return BadRequest(new { error = $"Channel is one of: {string.Join(", ", MessageChannels.All)}." });
        if (!string.IsNullOrWhiteSpace(dto.Product) && ProductCatalog.Canonical(dto.Product) == null)
            return BadRequest(new { error = $"'{dto.Product}' is not one of our products. Known: {string.Join(", ", ProductCatalog.Keys)}." });

        var leads = await _db.Leads.VisibleTo(_db, userId).Where(l => ids.Contains(l.Id)).ToListAsync();
        var haveDraft = (await _db.LeadMessages.AsNoTracking().Where(m => m.Status == MessageStatuses.Draft && m.Channel == ch && ids.Contains(m.LeadId))
            .Select(m => m.LeadId).ToListAsync()).ToHashSet();

        var created = new List<object>();
        var skipped = new List<object>();
        foreach (var lead in leads)
        {
            if (haveDraft.Contains(lead.Id)) { skipped.Add(new { leadId = lead.Id, name = lead.Name, reason = "There is already a draft for this channel." }); continue; }
            var prefill = await BuildPrefillAsync(lead, ch, dto.Product);
            var m = new LeadMessage { LeadId = lead.Id, UserId = userId!, Channel = ch, Subject = prefill.Subject, Body = prefill.Body };
            if (m.Body.Length == 0) { skipped.Add(new { leadId = lead.Id, name = lead.Name, reason = "The template gave an empty message." }); continue; }
            _db.LeadMessages.Add(m);
            created.Add(new { leadId = lead.Id, name = lead.Name, messageId = m.Id, warnings = prefill.Warnings });
        }
        foreach (var missing in ids.Except(leads.Select(l => l.Id))) skipped.Add(new { leadId = missing, name = (string?)null, reason = "Not one of your leads." });
        await _db.SaveChangesAsync();
        return Ok(new { created = created.Count, skipped = skipped.Count, items = created, skippedItems = skipped });
    }

    // ---- next to contact -----------------------------------------------------------------------------------------------------------

    // GET /api/messages/queue?take=20&minScore=6 : leads that are still New with a high score, best first
    [HttpGet("messages/queue")]
    public async Task<IActionResult> Queue([FromQuery] int take = 20, [FromQuery] int minScore = 6, [FromQuery] string? organizationId = null)
    {
        var userId = UserId;
        take = Math.Clamp(take, 1, 100);
        minScore = Math.Clamp(minScore, 0, 9);
        var leads = await _db.Leads.AsNoTracking()
            .VisibleTo(_db, userId).InOrganizations(await Organizations.ParseFilterAsync(_db, userId, organizationId)).Where(l => l.PipelineStatus == PipelineStatus.New && l.LastContactedAt == null && l.SalesPriorityScore >= minScore)
            .OrderByDescending(l => l.SalesPriorityScore).ThenByDescending(l => l.WhyLead != null).ThenBy(l => l.Name).ThenBy(l => l.Id)
            .Take(take).ToListAsync();
        var ids = leads.Select(l => l.Id).ToList();
        var drafts = (await _db.LeadMessages.AsNoTracking().Where(m => m.Status == MessageStatuses.Draft && ids.Contains(m.LeadId)).Select(m => m.LeadId).ToListAsync()).ToHashSet();
        return Ok(leads.Select(l => new QueueItemDto(l.Id, l.Name, l.SalesPriorityScore, l.SalesPriorityLabel, l.Sector, l.City, l.OwnerName, l.WhyLead, l.Needs,
            LeadInsight.ParseFit(l.ProductFitJson)?.Select(e => new ProductFitDto(e.Product, e.Reason, e.EvidenceQuote, e.SourceUrl)).ToList(), drafts.Contains(l.Id), l.LastContactedAt, l.OrganizationId)));
    }
}
