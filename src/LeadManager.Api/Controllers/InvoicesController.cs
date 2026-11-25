using System.Text.Json.Serialization;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

namespace LeadManager.Api.Controllers;

public record InvoiceDto(
    Guid Id, Guid ClientId, Guid? ServiceId, Guid? DocumentId, string Number,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime InvoiceDate,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? PeriodStart,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? PeriodEnd,
    decimal AmountExclVat, decimal VatRate, decimal AmountInclVat, string Status, string? SentTo,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? PaidAt, string? Notes, string? SourceRef,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? SentAt = null,
    bool SendUnknown = false, decimal? OutstandingAmount = null,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? SyncedAt = null);

public record UpsertInvoiceDto(
    string Number, decimal AmountExclVat,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? InvoiceDate = null,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? PeriodStart = null,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? PeriodEnd = null,
    decimal? VatRate = null, string? Status = null, string? SentTo = null,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? PaidAt = null,
    string? Notes = null, Guid? ServiceId = null, Guid? DocumentId = null, string? SourceRef = null);

public record ClientDueDto(Guid ClientId, string ClientName, List<DueItem> Items);

/// <summary>
/// Invoices sent to clients, tied to the service they are for, and the derived list of what is due next.
/// The books stay in Fedha; this is what lets Jengo answer "make an invoice for X" from the agreements.
/// </summary>
[ApiController]
[Authorize]
[AllowApiKey]
public class InvoicesController : ControllerBase
{
    private const int MaxRef = 100;
    private readonly LeadManagerDbContext _db;

    public InvoicesController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    public static InvoiceDto ToDto(ClientInvoice i) => new(
        i.Id, i.ClientId, i.ServiceId, i.DocumentId, i.Number, i.InvoiceDate, i.PeriodStart, i.PeriodEnd,
        i.AmountExclVat, i.VatRate, Math.Round(i.AmountExclVat * (1 + i.VatRate / 100m), 2), i.Status.ToString(), i.SentTo, i.PaidAt, i.Notes, i.SourceRef,
        i.SentAt, i.SendUnknown, i.OutstandingAmount, i.SyncedAt);

    private async Task<bool> OwnsClient(Guid clientId)
    {
        var userId = UserId;
        return !string.IsNullOrEmpty(userId) && await _db.Clients.AnyAsync(c => c.Id == clientId && c.CreatedByUserId == userId);
    }

    private async Task<ClientInvoice?> FindOwned(Guid id)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return null;
        return await _db.ClientInvoices.FirstOrDefaultAsync(i => i.Id == id
            && _db.Clients.Any(c => c.Id == i.ClientId && c.CreatedByUserId == userId));
    }

    private async Task<string?> Apply(ClientInvoice i, UpsertInvoiceDto dto)
    {
        var number = Clean(dto.Number);
        if (number == null) return "Invoice number is required.";
        if (TooLong(number, MaxRef)) return "Invoice number is too long.";
        var status = InvoiceStatus.Draft;
        if (!string.IsNullOrWhiteSpace(dto.Status) && !TryParseName(dto.Status, out status)) return "Unknown status.";
        if (dto.AmountExclVat is < -1_000_000_000m or > 1_000_000_000m) return "Amount is not valid.";
        if (dto.VatRate is < 0 or > 100m) return "VAT rate is not valid.";
        if (dto.PeriodStart.HasValue && dto.PeriodEnd.HasValue && dto.PeriodEnd < dto.PeriodStart) return "Period end is before the period start.";
        if ((dto.PeriodStart.HasValue) != (dto.PeriodEnd.HasValue)) return "Give both the period start and the period end, or neither.";
        var notes = Clean(dto.Notes);
        var sentTo = Clean(dto.SentTo);
        var sref = Clean(dto.SourceRef);
        if (TooLong(notes, MaxNotes) || TooLong(sentTo, MaxTitle) || TooLong(sref, MaxRef)) return "A text field is too long.";
        if (dto.ServiceId.HasValue && !await _db.ClientServices.AnyAsync(s => s.Id == dto.ServiceId && s.ClientId == i.ClientId))
            return "Service does not belong to this client.";
        if (dto.DocumentId.HasValue && !await _db.ClientDocuments.AnyAsync(d => d.Id == dto.DocumentId && d.ClientId == i.ClientId))
            return "Document does not belong to this client.";

        i.Number = number;
        i.Status = status;
        i.AmountExclVat = Math.Round(dto.AmountExclVat, 2);
        i.VatRate = dto.VatRate ?? i.VatRate;
        i.InvoiceDate = dto.InvoiceDate.HasValue ? UtcDateTimeConverter.Normalize(dto.InvoiceDate.Value) : i.InvoiceDate;
        i.PeriodStart = dto.PeriodStart.HasValue ? UtcDateTimeConverter.Normalize(dto.PeriodStart.Value) : null;
        i.PeriodEnd = dto.PeriodEnd.HasValue ? UtcDateTimeConverter.Normalize(dto.PeriodEnd.Value) : null;
        i.SentTo = sentTo;
        i.PaidAt = dto.PaidAt.HasValue ? UtcDateTimeConverter.Normalize(dto.PaidAt.Value) : (status == InvoiceStatus.Paid ? i.PaidAt : null);
        i.Notes = notes;
        i.SourceRef = sref;
        i.ServiceId = dto.ServiceId;
        i.DocumentId = dto.DocumentId;
        return null;
    }

    // GET /api/clients/{clientId}/invoices
    [HttpGet("api/clients/{clientId:guid}/invoices")]
    public async Task<IActionResult> List(Guid clientId)
    {
        if (!await OwnsClient(clientId)) return NotFound();
        var rows = await _db.ClientInvoices.Where(i => i.ClientId == clientId).OrderByDescending(i => i.InvoiceDate).ToListAsync();
        return Ok(rows.Select(ToDto));
    }

    // POST /api/clients/{clientId}/invoices  (a repeated SourceRef, or the same Number, updates the existing invoice)
    [HttpPost("api/clients/{clientId:guid}/invoices")]
    public async Task<IActionResult> Create(Guid clientId, [FromBody] UpsertInvoiceDto dto)
    {
        if (!await OwnsClient(clientId)) return NotFound();
        var sref = Clean(dto.SourceRef);
        var number = Clean(dto.Number);
        var existing = await _db.ClientInvoices.FirstOrDefaultAsync(i => i.ClientId == clientId
            && ((sref != null && i.SourceRef == sref) || (number != null && i.Number == number)));
        if (existing != null)
        {
            var upErr = await Apply(existing, dto);
            if (upErr != null) return BadRequest(new { error = upErr });
            await _db.SaveChangesAsync();
            return Ok(ToDto(existing));
        }
        var inv = new ClientInvoice { ClientId = clientId, CreatedByUserId = UserId };
        var error = await Apply(inv, dto);
        if (error != null) return BadRequest(new { error });
        _db.ClientInvoices.Add(inv);
        await _db.SaveChangesAsync();
        return Created($"api/invoices/{inv.Id}", ToDto(inv));
    }

    // PUT /api/invoices/{id}
    [HttpPut("api/invoices/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertInvoiceDto dto)
    {
        var inv = await FindOwned(id);
        if (inv == null) return NotFound();
        var error = await Apply(inv, dto);
        if (error != null) return BadRequest(new { error });
        await _db.SaveChangesAsync();
        return Ok(ToDto(inv));
    }

    // DELETE /api/invoices/{id}  (cancel instead of delete where possible)
    [HttpDelete("api/invoices/{id:guid}")]
    [DenyApiKey]
    public async Task<IActionResult> Delete(Guid id)
    {
        var inv = await FindOwned(id);
        if (inv == null) return NotFound();
        _db.ClientInvoices.Remove(inv);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/invoicing/due?clientId=&state=Due   what to invoice next, derived from the agreed terms and the invoices sent
    [HttpGet("api/invoicing/due")]
    public async Task<IActionResult> Due([FromQuery] Guid? clientId, [FromQuery] string? state, [FromQuery] DateTime? asOf)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var clientsQ = _db.Clients.Where(c => c.CreatedByUserId == userId && c.IsActive);
        if (clientId.HasValue) clientsQ = clientsQ.Where(c => c.Id == clientId);
        var clients = await clientsQ.OrderBy(c => c.Name).ToListAsync();
        if (clientId.HasValue && clients.Count == 0) return NotFound();
        var ids = clients.Select(c => c.Id).ToList();
        var services = await _db.ClientServices.Where(s => ids.Contains(s.ClientId)).ToListAsync();
        var invoices = await _db.ClientInvoices.Where(i => ids.Contains(i.ClientId)).ToListAsync();
        var today = (asOf ?? DateTime.UtcNow).Date;

        var result = new List<ClientDueDto>();
        foreach (var c in clients)
        {
            var items = services.Where(s => s.ClientId == c.Id)
                .Select(s => BillingCalculator.Compute(s, invoices.Where(i => i.ServiceId == s.Id), today))
                .Where(d => string.IsNullOrWhiteSpace(state) || string.Equals(d.State, state, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (items.Count > 0) result.Add(new ClientDueDto(c.Id, c.Name, items));
        }
        return Ok(result);
    }
}
