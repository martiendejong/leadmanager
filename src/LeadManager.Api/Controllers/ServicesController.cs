using System.Text.Json.Serialization;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

namespace LeadManager.Api.Controllers;

public record ServiceDto(
    Guid Id, Guid ClientId, string Kind, string Status, string Name, Guid? ProjectId,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? StartDate,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? EndDate,
    decimal? MonthlyAmount, string? Notes, string? SourceRef,
    string BillingInterval = "Monthly", decimal? BillingAmount = null, decimal? HourlyRate = null, decimal? VatRate = null,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? PaidUntil = null,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? FreeUntil = null,
    string? FreeReason = null, string? InvoiceNote = null);

public record UpsertServiceDto(
    string Kind, string? Name, string? Status,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? StartDate,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? EndDate,
    decimal? MonthlyAmount, string? Notes, Guid? ProjectId, string? SourceRef = null,
    string? BillingInterval = null, decimal? BillingAmount = null, decimal? HourlyRate = null, decimal? VatRate = null,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? PaidUntil = null,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? FreeUntil = null,
    string? FreeReason = null, string? InvoiceNote = null);

/// <summary>The services we deliver to a client, such as website management. Documents link to them.</summary>
[ApiController]
[Authorize]
[AllowApiKey]
public class ServicesController : ControllerBase
{
    private const int MaxRef = 100;
    private readonly LeadManagerDbContext _db;

    public ServicesController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    public static ServiceDto ToDto(ClientService s) => new(
        s.Id, s.ClientId, s.Kind.ToString(), s.Status.ToString(), s.Name, s.ProjectId, s.StartDate, s.EndDate,
        s.MonthlyAmount, s.Notes, s.SourceRef,
        s.BillingInterval.ToString(), s.BillingAmount, s.HourlyRate, s.VatRate, s.PaidUntil, s.FreeUntil, s.FreeReason, s.InvoiceNote);

    private async Task<bool> OwnsClient(Guid clientId)
    {
        var userId = UserId;
        return !string.IsNullOrEmpty(userId) && await _db.Clients.AnyAsync(c => c.Id == clientId && c.CreatedByUserId == userId);
    }

    private async Task<ClientService?> FindOwned(Guid id)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return null;
        return await _db.ClientServices.FirstOrDefaultAsync(s => s.Id == id
            && _db.Clients.Any(c => c.Id == s.ClientId && c.CreatedByUserId == userId));
    }

    public static string DefaultName(ServiceKind kind) => kind switch
    {
        ServiceKind.WebsiteManagement => "Website management",
        ServiceKind.Hosting => "Hosting",
        ServiceKind.Maintenance => "Maintenance",
        ServiceKind.Development => "Development",
        ServiceKind.Consulting => "Consulting",
        _ => "Service",
    };

    private async Task<string?> Apply(ClientService s, UpsertServiceDto dto)
    {
        if (!TryParseName<ServiceKind>(dto.Kind, out var kind)) return "Unknown service kind.";
        var status = ServiceStatus.Proposed;
        if (!string.IsNullOrWhiteSpace(dto.Status) && !TryParseName(dto.Status, out status)) return "Unknown status.";
        var name = Clean(dto.Name) ?? DefaultName(kind);
        if (TooLong(name, MaxTitle)) return "Name is too long.";
        var notes = Clean(dto.Notes);
        if (TooLong(notes, MaxNotes)) return "Notes are too long.";
        var sref = Clean(dto.SourceRef);
        if (TooLong(sref, MaxRef)) return "Source reference is too long.";
        if (dto.MonthlyAmount is < 0 or > 1_000_000_000m) return "Amount is not valid.";
        if (dto.StartDate.HasValue && dto.EndDate.HasValue && dto.EndDate < dto.StartDate) return "End date is before the start date.";
        if (dto.ProjectId.HasValue && !await _db.Projects.AnyAsync(p => p.Id == dto.ProjectId && p.ClientId == s.ClientId))
            return "Project does not belong to this client.";

        // Billing terms. Only fields that are sent change, so a client that knows nothing of billing keeps them.
        var interval = s.BillingInterval;
        if (!string.IsNullOrWhiteSpace(dto.BillingInterval) && !TryParseName(dto.BillingInterval, out interval)) return "Unknown billing interval.";
        if (dto.BillingAmount is < 0 or > 1_000_000_000m || dto.HourlyRate is < 0 or > 1_000_000m) return "Billing amount is not valid.";
        if (dto.VatRate is < 0 or > 100m) return "VAT rate is not valid.";
        var freeReason = Clean(dto.FreeReason);
        var invoiceNote = Clean(dto.InvoiceNote);
        if (TooLong(freeReason, MaxTitle) || TooLong(invoiceNote, MaxNotes)) return "Free reason or invoice note is too long.";

        s.BillingInterval = interval;
        if (dto.BillingAmount.HasValue) s.BillingAmount = Math.Round(dto.BillingAmount.Value, 2);
        if (dto.HourlyRate.HasValue) s.HourlyRate = Math.Round(dto.HourlyRate.Value, 2);
        if (dto.VatRate.HasValue) s.VatRate = dto.VatRate;
        if (dto.PaidUntil.HasValue) s.PaidUntil = UtcDateTimeConverter.Normalize(dto.PaidUntil.Value);
        if (dto.FreeUntil.HasValue) s.FreeUntil = UtcDateTimeConverter.Normalize(dto.FreeUntil.Value);
        if (freeReason != null) s.FreeReason = freeReason;
        if (invoiceNote != null) s.InvoiceNote = invoiceNote;

        s.Kind = kind;
        s.Status = status;
        s.Name = name;
        s.Notes = notes;
        s.SourceRef = sref;
        s.ProjectId = dto.ProjectId;
        s.MonthlyAmount = dto.MonthlyAmount.HasValue ? Math.Round(dto.MonthlyAmount.Value, 2) : null;
        // Keep the informative monthly figure in line with the billing amount when only the latter is sent.
        if (!dto.MonthlyAmount.HasValue && s.BillingAmount.HasValue && BillingCalculator.Months(interval) > 0)
            s.MonthlyAmount = Math.Round(s.BillingAmount.Value / BillingCalculator.Months(interval), 2);
        s.StartDate = dto.StartDate.HasValue ? UtcDateTimeConverter.Normalize(dto.StartDate.Value) : null;
        s.EndDate = dto.EndDate.HasValue ? UtcDateTimeConverter.Normalize(dto.EndDate.Value) : null;
        return null;
    }

    // GET /api/clients/{clientId}/services
    [HttpGet("api/clients/{clientId:guid}/services")]
    public async Task<IActionResult> List(Guid clientId)
    {
        if (!await OwnsClient(clientId)) return NotFound();
        var rows = await _db.ClientServices.Where(s => s.ClientId == clientId).OrderBy(s => s.Kind).ThenBy(s => s.StartDate).ToListAsync();
        return Ok(rows.Select(ToDto));
    }

    // POST /api/clients/{clientId}/services  (a repeated SourceRef updates the existing service)
    [HttpPost("api/clients/{clientId:guid}/services")]
    public async Task<IActionResult> Create(Guid clientId, [FromBody] UpsertServiceDto dto)
    {
        if (!await OwnsClient(clientId)) return NotFound();
        var sref = Clean(dto.SourceRef);
        if (sref != null)
        {
            var existing = await _db.ClientServices.FirstOrDefaultAsync(s => s.ClientId == clientId && s.SourceRef == sref);
            if (existing != null)
            {
                var upErr = await Apply(existing, dto);
                if (upErr != null) return BadRequest(new { error = upErr });
                await _db.SaveChangesAsync();
                return Ok(ToDto(existing));
            }
        }
        var svc = new ClientService { ClientId = clientId, CreatedByUserId = UserId };
        var error = await Apply(svc, dto);
        if (error != null) return BadRequest(new { error });
        _db.ClientServices.Add(svc);
        await _db.SaveChangesAsync();
        return Created($"api/services/{svc.Id}", ToDto(svc));
    }

    // PUT /api/services/{id}
    [HttpPut("api/services/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertServiceDto dto)
    {
        var svc = await FindOwned(id);
        if (svc == null) return NotFound();
        var error = await Apply(svc, dto);
        if (error != null) return BadRequest(new { error });
        await _db.SaveChangesAsync();
        return Ok(ToDto(svc));
    }

    // DELETE /api/services/{id}  (documents stay, without the link)
    [HttpDelete("api/services/{id:guid}")]
    [DenyApiKey]
    public async Task<IActionResult> Delete(Guid id)
    {
        var svc = await FindOwned(id);
        if (svc == null) return NotFound();
        _db.ClientServices.Remove(svc);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
