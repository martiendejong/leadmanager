using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

public record AgreementDocDto(
    Guid Id, string Kind, string Status, string Title, string? Reference, int Version, Guid? ServiceId,
    DateTime DocumentDate, DateTime? SignedAt, decimal? Amount, int FileCount, string? Url);

public record ClientAgreementsDto(
    Guid ClientId, string ClientName, string? EngagementCategory,
    List<ServiceDto> Services, List<AgreementDocDto> Contracts, List<AgreementDocDto> Concepts);

/// <summary>
/// One view over all clients: which services we deliver, which contracts were drawn up and sent (or signed),
/// and which documents are still concepts. A contract is a non-draft agreement-like document; a concept is a draft.
/// </summary>
[ApiController]
[Authorize]
[AllowApiKey]
public class AgreementsController : ControllerBase
{
    private static readonly DocumentKind[] ContractKinds =
        { DocumentKind.Agreement, DocumentKind.Order, DocumentKind.Dpa, DocumentKind.Nda, DocumentKind.Quote };

    private readonly LeadManagerDbContext _db;

    public AgreementsController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/agreements?service=WebsiteManagement&onlyWithServices=true
    [HttpGet("api/agreements")]
    public async Task<IActionResult> Overview([FromQuery] string? service, [FromQuery] bool onlyWithServices = false)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        ServiceKind? filter = null;
        if (!string.IsNullOrWhiteSpace(service))
        {
            if (!Enum.TryParse<ServiceKind>(service, true, out var parsed)) return BadRequest(new { error = "Unknown service kind." });
            filter = parsed;
        }

        var clients = await _db.Clients.Where(c => c.CreatedByUserId == userId && c.IsActive).OrderBy(c => c.Name).ToListAsync();
        var ids = clients.Select(c => c.Id).ToList();
        var services = await _db.ClientServices.Where(s => ids.Contains(s.ClientId)).ToListAsync();
        var docs = await _db.ClientDocuments.Include(d => d.Files).Where(d => ids.Contains(d.ClientId)).ToListAsync();

        static AgreementDocDto Map(ClientDocument d) => new(d.Id, d.Kind.ToString(), d.Status.ToString(), d.Title, d.Reference,
            d.Version, d.ServiceId, d.DocumentDate, d.SignedAt, d.Amount, d.Files.Count, d.Url);

        var result = new List<ClientAgreementsDto>();
        foreach (var c in clients)
        {
            var svc = services.Where(s => s.ClientId == c.Id).OrderBy(s => s.Kind).ToList();
            if (filter.HasValue && !svc.Any(s => s.Kind == filter && s.Status != ServiceStatus.Ended)) continue;
            if (onlyWithServices && svc.Count == 0) continue;

            var mine = docs.Where(d => d.ClientId == c.Id).OrderByDescending(d => d.DocumentDate).ToList();
            var contracts = mine.Where(d => d.Status != DocumentStatus.Draft && ContractKinds.Contains(d.Kind)).Select(Map).ToList();
            var concepts = mine.Where(d => d.Status == DocumentStatus.Draft).Select(Map).ToList();
            result.Add(new ClientAgreementsDto(c.Id, c.Name, c.EngagementCategory, svc.Select(ServicesController.ToDto).ToList(), contracts, concepts));
        }
        return Ok(result);
    }
}
