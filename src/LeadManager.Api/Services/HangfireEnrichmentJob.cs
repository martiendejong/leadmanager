using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services;

public class HangfireEnrichmentJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HangfireEnrichmentJob> _logger;
    private const int BatchSize = 10;
    // A lead that fails MaxAttempts times stays unenriched until someone looks at
    // it; without this cap the sweep retried failing leads forever, which is what
    // kept the 09-09 memory-restart-storm alive.
    private const int MaxAttempts = 5;
    private const int RetryDelayMinutes = 30;

    public HangfireEnrichmentJob(IServiceScopeFactory scopeFactory, ILogger<HangfireEnrichmentJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();
        var channel = scope.ServiceProvider.GetRequiredService<EnrichmentChannel>();

        var now = DateTime.UtcNow;
        var retryThreshold = now.AddMinutes(-RetryDelayMinutes);

        // Pick leads that need enrichment:
        // - IsEnriched = false
        // - fewer than MaxAttempts tries so far
        // - LastEnrichmentAttempt is null (never tried) OR older than the retry delay
        var leadsToEnrich = await db.Leads
            .Where(l => !l.IsEnriched
                     && l.EnrichmentAttempts < MaxAttempts
                     && (l.LastEnrichmentAttempt == null || l.LastEnrichmentAttempt < retryThreshold))
            .OrderBy(l => l.CreatedAt)
            .Take(BatchSize)
            .ToListAsync();

        if (leadsToEnrich.Count == 0)
        {
            _logger.LogDebug("Enrichment sweep: no leads to enrich");
            return;
        }

        // Create a single EnrichmentJob for this batch
        var job = new EnrichmentJob
        {
            Id = Guid.NewGuid(),
            LeadIds = leadsToEnrich.Select(l => l.Id).ToList(),
            TotalLeads = leadsToEnrich.Count,
            Status = "Queued",
            CreatedAt = now
        };

        db.EnrichmentJobs.Add(job);

        // Stamp LastEnrichmentAttempt to prevent duplicate queueing
        foreach (var lead in leadsToEnrich)
        {
            lead.LastEnrichmentAttempt = now;
            lead.EnrichmentAttempts++;
            if (lead.EnrichmentAttempts >= MaxAttempts)
                _logger.LogWarning(
                    "Lead {LeadId} ({Name}) reached {MaxAttempts} enrichment attempts; sweep will stop retrying it",
                    lead.Id, lead.Name, MaxAttempts);
        }

        await db.SaveChangesAsync();

        // Enqueue via the existing channel — EnrichmentBackgroundService does the actual work
        await channel.Writer.WriteAsync(job.Id);

        _logger.LogInformation(
            "Enrichment sweep queued job {JobId} with {Count} leads",
            job.Id, leadsToEnrich.Count);
    }
}
