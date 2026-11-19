using System.Text.Json.Serialization;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

namespace LeadManager.Api.Controllers;

public record DocumentFileDto(
    Guid Id, string FileName, string ContentType, long SizeBytes, string? Label,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime UploadedAt);

public record DocumentDto(
    Guid Id, Guid ClientId, Guid? ProjectId, string? ProjectName, Guid? PredecessorId,
    string Kind, string Status, string Title, string? Reference,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime DocumentDate,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? SignedAt,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? ValidUntil,
    decimal? Amount, string? Notes, string? Url,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime CreatedAt,
    List<DocumentFileDto> Files);

public record UpsertDocumentDto(
    string Kind, string Title, string? Status, string? Reference,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? DocumentDate,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? SignedAt,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? ValidUntil,
    decimal? Amount, string? Notes, string? Url, Guid? ProjectId, Guid? PredecessorId);

/// <summary>
/// The dossier of a client: quotes, orders and signed agreements (processing agreement, NDA, ...), each with
/// its files. A document can follow from another one, so the line quote -> order -> agreement stays visible.
/// </summary>
[ApiController]
[Authorize]
[AllowApiKey]
public class DocumentsController : ControllerBase
{
    private const int MaxReference = 100;
    private readonly LeadManagerDbContext _db;
    private readonly DocumentStorage _storage;

    public DocumentsController(LeadManagerDbContext db, DocumentStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private static DocumentDto ToDto(ClientDocument d, string? projectName) => new(
        d.Id, d.ClientId, d.ProjectId, projectName, d.PredecessorId, d.Kind.ToString(), d.Status.ToString(), d.Title,
        d.Reference, d.DocumentDate, d.SignedAt, d.ValidUntil, d.Amount, d.Notes, d.Url, d.CreatedAt,
        d.Files.OrderBy(f => f.UploadedAt).Select(f => new DocumentFileDto(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.Label, f.UploadedAt)).ToList());

    private async Task<bool> OwnsClient(Guid clientId)
    {
        var userId = UserId;
        return !string.IsNullOrEmpty(userId) && await _db.Clients.AnyAsync(c => c.Id == clientId && c.CreatedByUserId == userId);
    }

    private async Task<ClientDocument?> FindOwned(Guid id)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return null;
        return await _db.ClientDocuments.Include(d => d.Files).FirstOrDefaultAsync(d => d.Id == id
            && _db.Clients.Any(c => c.Id == d.ClientId && c.CreatedByUserId == userId));
    }

    private async Task<string?> ProjectName(Guid? projectId) =>
        projectId == null ? null : await _db.Projects.Where(p => p.Id == projectId).Select(p => p.Name).FirstOrDefaultAsync();

    // GET /api/clients/{clientId}/documents
    [HttpGet("api/clients/{clientId:guid}/documents")]
    public async Task<IActionResult> List(Guid clientId)
    {
        if (!await OwnsClient(clientId)) return NotFound();
        var docs = await _db.ClientDocuments.Include(d => d.Files)
            .Where(d => d.ClientId == clientId)
            .OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.CreatedAt)
            .ToListAsync();
        var names = await _db.Projects.Where(p => p.ClientId == clientId).ToDictionaryAsync(p => p.Id, p => p.Name);
        return Ok(docs.Select(d => ToDto(d, d.ProjectId.HasValue && names.TryGetValue(d.ProjectId.Value, out var n) ? n : null)));
    }

    /// <summary>Validates the body and the links to a project and a predecessor; fills <paramref name="doc"/>.</summary>
    private async Task<string?> Apply(ClientDocument doc, UpsertDocumentDto dto)
    {
        if (!TryParseName<DocumentKind>(dto.Kind, out var kind)) return "Onbekend soort document.";
        var status = DocumentStatus.Draft;
        if (!string.IsNullOrWhiteSpace(dto.Status) && !TryParseName(dto.Status, out status)) return "Onbekende status.";

        var title = Clean(dto.Title);
        if (title == null) return "Titel is verplicht.";
        if (TooLong(title, MaxTitle)) return "Titel is te lang.";
        var reference = Clean(dto.Reference);
        if (TooLong(reference, MaxReference)) return "Kenmerk is te lang.";
        var notes = Clean(dto.Notes);
        if (TooLong(notes, MaxNotes)) return "Notities zijn te lang.";
        var url = Clean(dto.Url);
        if (url != null && !url.Contains("://")) url = "https://" + url;
        if (url != null && !IsSafeHttpUrl(url)) return "Link moet een geldig http(s)-adres zijn.";
        if (dto.Amount is < 0 or > 1_000_000_000m) return "Bedrag is niet geldig.";

        if (dto.ProjectId.HasValue && !await _db.Projects.AnyAsync(p => p.Id == dto.ProjectId && p.ClientId == doc.ClientId))
            return "Project hoort niet bij deze klant.";

        if (dto.PredecessorId.HasValue)
        {
            if (dto.PredecessorId == doc.Id) return "Een document kan niet op zichzelf volgen.";
            // Same client, and walking back up the chain must never meet this document (no loops).
            var current = dto.PredecessorId;
            for (var i = 0; current.HasValue; i++)
            {
                if (i > 50) return "De keten van documenten is te lang.";
                var prev = await _db.ClientDocuments.Where(d => d.Id == current).Select(d => new { d.ClientId, d.PredecessorId }).FirstOrDefaultAsync();
                if (prev == null || prev.ClientId != doc.ClientId) return "Voorgaand document hoort niet bij deze klant.";
                if (prev.PredecessorId == doc.Id) return "Dit zou een kringverwijzing maken.";
                current = prev.PredecessorId;
            }
        }

        doc.Kind = kind;
        doc.Status = status;
        doc.Title = title;
        doc.Reference = reference;
        doc.Notes = notes;
        doc.Url = url;
        doc.Amount = dto.Amount.HasValue ? Math.Round(dto.Amount.Value, 2) : null;
        doc.DocumentDate = UtcDateTimeConverter.Normalize(dto.DocumentDate ?? DateTime.UtcNow);
        doc.ValidUntil = dto.ValidUntil.HasValue ? UtcDateTimeConverter.Normalize(dto.ValidUntil.Value) : null;
        doc.SignedAt = dto.SignedAt.HasValue ? UtcDateTimeConverter.Normalize(dto.SignedAt.Value) : null;
        if (status == DocumentStatus.Signed && doc.SignedAt == null) doc.SignedAt = doc.DocumentDate;
        doc.ProjectId = dto.ProjectId;
        doc.PredecessorId = dto.PredecessorId;
        return null;
    }

    // POST /api/clients/{clientId}/documents
    [HttpPost("api/clients/{clientId:guid}/documents")]
    public async Task<IActionResult> Create(Guid clientId, [FromBody] UpsertDocumentDto dto)
    {
        if (!await OwnsClient(clientId)) return NotFound();
        var doc = new ClientDocument { ClientId = clientId, CreatedByUserId = UserId };
        var error = await Apply(doc, dto);
        if (error != null) return BadRequest(new { error });

        _db.ClientDocuments.Add(doc);
        await _db.SaveChangesAsync();
        return Created($"api/documents/{doc.Id}", ToDto(doc, await ProjectName(doc.ProjectId)));
    }

    // PUT /api/documents/{id}
    [HttpPut("api/documents/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertDocumentDto dto)
    {
        var doc = await FindOwned(id);
        if (doc == null) return NotFound();
        var error = await Apply(doc, dto);
        if (error != null) return BadRequest(new { error });
        await _db.SaveChangesAsync();
        return Ok(ToDto(doc, await ProjectName(doc.ProjectId)));
    }

    // DELETE /api/documents/{id}  (its files go too; documents that followed from it stay, without the link)
    [HttpDelete("api/documents/{id:guid}")]
    [DenyApiKey]
    public async Task<IActionResult> Delete(Guid id)
    {
        var doc = await FindOwned(id);
        if (doc == null) return NotFound();
        var paths = doc.Files.Select(f => f.StoragePath).ToList();
        _db.ClientDocuments.Remove(doc);
        await _db.SaveChangesAsync();
        foreach (var p in paths) _storage.TryDelete(p);
        return NoContent();
    }

    // POST /api/documents/{id}/files  (multipart: file, optional label)
    [HttpPost("api/documents/{id:guid}/files")]
    [RequestSizeLimit(DocumentStorage.MaxFileBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = DocumentStorage.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid id, IFormFile? file, [FromForm] string? label, CancellationToken ct)
    {
        var doc = await FindOwned(id);
        if (doc == null) return NotFound();
        if (file == null || file.Length == 0) return BadRequest(new { error = "Kies een bestand." });
        if (file.Length > DocumentStorage.MaxFileBytes) return BadRequest(new { error = "Bestand is groter dan 25 MB." });
        if (doc.Files.Count >= DocumentStorage.MaxFilesPerDocument) return BadRequest(new { error = "Er passen maximaal 20 bestanden bij één document." });

        var name = DocumentStorage.CleanName(file.FileName);
        var contentType = DocumentStorage.ContentTypeFor(name);
        if (contentType == null) return BadRequest(new { error = $"Dit bestandstype mag niet. Toegestaan: {DocumentStorage.AllowedExtensions}." });
        var cleanLabel = Clean(label);
        if (TooLong(cleanLabel, MaxLabel)) return BadRequest(new { error = "Label is te lang." });

        string storagePath;
        long size;
        string sha;
        try
        {
            await using var stream = file.OpenReadStream();
            (storagePath, size, sha) = await _storage.SaveAsync(stream, ct);
        }
        catch (InvalidDataException)
        {
            return BadRequest(new { error = "Bestand is groter dan 25 MB." });
        }

        var row = new ClientDocumentFile
        {
            DocumentId = doc.Id, FileName = name, ContentType = contentType, SizeBytes = size, Sha256 = sha,
            StoragePath = storagePath, Label = cleanLabel, UploadedByUserId = UserId,
        };
        _db.ClientDocumentFiles.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            _storage.TryDelete(storagePath);
            throw;
        }
        return Created($"api/documents/files/{row.Id}",
            new DocumentFileDto(row.Id, row.FileName, row.ContentType, row.SizeBytes, row.Label, row.UploadedAt));
    }

    private async Task<ClientDocumentFile?> FindOwnedFile(Guid fileId)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return null;
        return await _db.ClientDocumentFiles.FirstOrDefaultAsync(f => f.Id == fileId
            && _db.ClientDocuments.Any(d => d.Id == f.DocumentId
                && _db.Clients.Any(c => c.Id == d.ClientId && c.CreatedByUserId == userId)));
    }

    // GET /api/documents/files/{fileId}  (always a download, never rendered by the browser)
    [HttpGet("api/documents/files/{fileId:guid}")]
    [DenyApiKey]
    public async Task<IActionResult> Download(Guid fileId)
    {
        var row = await FindOwnedFile(fileId);
        if (row == null) return NotFound();
        var stream = _storage.Open(row.StoragePath);
        if (stream == null) return NotFound(new { error = "Het bestand ontbreekt op de server." });

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return File(stream, row.ContentType, row.FileName);
    }

    // DELETE /api/documents/files/{fileId}
    [HttpDelete("api/documents/files/{fileId:guid}")]
    [DenyApiKey]
    public async Task<IActionResult> DeleteFile(Guid fileId)
    {
        var row = await FindOwnedFile(fileId);
        if (row == null) return NotFound();
        var path = row.StoragePath;
        _db.ClientDocumentFiles.Remove(row);
        await _db.SaveChangesAsync();
        _storage.TryDelete(path);
        return NoContent();
    }
}
