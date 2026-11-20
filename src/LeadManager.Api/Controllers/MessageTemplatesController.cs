using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Hunt;
using LeadManager.Api.Services.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>
/// The message templates of the signed in user, per channel and per product key of the catalog (an empty product key is the default of the
/// channel). Written in the language of the readers (Dutch). The prefill of a message uses them and falls back to the built-in templates.
/// Only for a signed in person (not for API keys).
/// </summary>
[ApiController]
[Authorize]
[Route("api/message-templates")]
public class MessageTemplatesController : ControllerBase
{
    private readonly LeadManagerDbContext _db;

    public MessageTemplatesController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private static object ToDto(MessageTemplate t) => new { id = t.Id, channel = t.Channel, productKey = t.ProductKey, subject = t.Subject, body = t.Body, updatedAt = t.UpdatedAt };

    // GET /api/message-templates : the user's templates, the built-in ones and what a template may use
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var mine = await _db.MessageTemplates.AsNoTracking().Where(t => t.UserId == UserId).OrderBy(t => t.Channel).ThenBy(t => t.ProductKey).ToListAsync();
        return Ok(new
        {
            placeholders = new[]
            {
                new { name = "name", meaning = "the business name" },
                new { name = "owner", meaning = "the first name of the owner (a neutral greeting when unknown)" },
                new { name = "why", meaning = "the verified quote that shows why the business is interesting (the business's own words)" },
                new { name = "need", meaning = "a quote from the business's own pages that shows a need (their own words, in their language); empty when no quote is stored" },
                new { name = "product", meaning = "the name of the product that fits" },
                new { name = "benefit", meaning = "one sentence on what that product does for the reader, ready written in Dutch or English to match the lead" },
                new { name = "sender", meaning = "your name (from your account), for the sign-off" },
                new { name = "reason", meaning = "the internal reason why the product fits. It is stored in English, so only use it in English texts; for Dutch use {benefit}" },
            },
            rule = "A line with a placeholder that has no value is left out. Nothing is invented.",
            channels = MessageChannels.All,
            products = ProductCatalog.All.Select(p => new { key = p.Key, name = p.Name }),
            templates = mine.Select(ToDto),
            builtIn = MessageTemplates.All.Select(b => new { channel = b.Channel, language = b.Language, subject = b.Subject, body = b.Body }),
        });
    }

    // PUT /api/message-templates : create or replace the template of one channel and product
    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] UpsertTemplateDto dto)
    {
        var ch = MessageChannels.Canonical(dto.Channel);
        if (ch == null) return BadRequest(new { error = $"Channel is one of: {string.Join(", ", MessageChannels.All)}." });
        var productKey = "";
        if (!string.IsNullOrWhiteSpace(dto.ProductKey))
        {
            productKey = ProductCatalog.Canonical(dto.ProductKey) ?? "";
            if (productKey == "") return BadRequest(new { error = $"'{dto.ProductKey}' is not one of our products. Known: {string.Join(", ", ProductCatalog.Keys)}." });
        }
        var subject = MessageRules.SingleLine(dto.Subject, MessageRules.MaxSubject);
        var body = MessageRules.Body(dto.Body);
        if (body.Length == 0) return BadRequest(new { error = "The template cannot be empty." });
        if (body.Length > MessageRules.MaxBody || (dto.Subject ?? "").Length > MessageRules.MaxSubject * 2) return BadRequest(new { error = $"A subject is at most {MessageRules.MaxSubject} and a template at most {MessageRules.MaxBody} characters." });
        var unknown = MessageTemplates.UnknownPlaceholders(subject).Concat(MessageTemplates.UnknownPlaceholders(body)).Distinct().ToList();
        if (unknown.Count > 0)
            return BadRequest(new { error = $"Unknown placeholder {string.Join(", ", unknown.Select(u => "{" + u + "}"))}. Allowed: {string.Join(", ", MessageTemplates.Placeholders.Select(p => "{" + p + "}"))}." });
        if (ch != MessageChannels.Email) subject = "";

        var t = await _db.MessageTemplates.FirstOrDefaultAsync(x => x.UserId == UserId && x.Channel == ch && x.ProductKey == productKey);
        if (t == null) { t = new MessageTemplate { UserId = UserId!, Channel = ch, ProductKey = productKey }; _db.MessageTemplates.Add(t); }
        t.Subject = subject; t.Body = body; t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(t));
    }

    // DELETE /api/message-templates/{id} : back to the built-in template
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var t = await _db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (t == null) return NotFound();
        _db.MessageTemplates.Remove(t);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
