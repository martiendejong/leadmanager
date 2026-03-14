using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

[ApiController]
[Route("api/leads/enrich")]
[Authorize]
public class EnrichmentController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly EnrichmentChannel _channel;

    public EnrichmentController(LeadManagerDbContext db, EnrichmentChannel channel)
    {
        _db = db;
        _channel = channel;
    }

    [HttpPost]
    public async Task<IActionResult> StartEnrichment([FromBody] EnrichmentRequest request)
    {
        if (request.Ids == null || request.Ids.Length == 0)
            return BadRequest("No lead IDs provided.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        // Only allow enriching leads owned by this user
        var validIds = await _db.Leads
            .Where(l => request.Ids.Contains(l.Id) && l.ImportedByUserId == userId)
            .Select(l => l.Id)
            .ToListAsync();

        if (validIds.Count == 0)
            return BadRequest("None of the provided lead IDs belong to your account.");

        var job = new EnrichmentJob
        {
            LeadIds = validIds,
            TotalLeads = validIds.Count,
            RequestedByUserId = userId
        };

        _db.EnrichmentJobs.Add(job);
        await _db.SaveChangesAsync();

        await _channel.Writer.WriteAsync(job.Id);

        return Accepted(new { jobId = job.Id });
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetJobStatus(Guid jobId)
    {
        var job = await _db.EnrichmentJobs.FindAsync(jobId);
        if (job == null)
            return NotFound();

        return Ok(new
        {
            id = job.Id,
            status = job.Status,
            totalLeads = job.TotalLeads,
            processedLeads = job.ProcessedLeads,
            successCount = job.SuccessCount,
            errorCount = job.ErrorCount,
            createdAt = job.CreatedAt,
            completedAt = job.CompletedAt
        });
    }
}

public class EnrichmentRequest
{
    public Guid[] Ids { get; set; } = [];
}
