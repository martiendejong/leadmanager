using Hangfire;
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
    private const int MaxRetries = 3;

    public HangfireEnrichmentJob(IServiceScopeFactory scopeFactory, ILogger<HangfireEnrichmentJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Sweep-level retry: if the sweep throws, Hangfire retries with exponential backoff.
    // Per-lead retry/backoff is handled via EnrichmentRetryCount + LastEnrichmentAttempt below.
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        try
        {
            await DoSweepAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enrichment sweep failed; Hangfire will retry with exponential backoff");
            throw;
        }
    }

    private async Task DoSweepAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();
        var channel = scope.ServiceProvider.GetRequiredService<EnrichmentChannel>();

        var now = DateTime.UtcNow;

        // Per-lead exponential backoff. EF LINQ cannot translate Math.Pow, so we
        // compute the cutoffs in C# and reference them in the query.
        //   retry 0 (never tried)      → eligible immediately
        //   retry 1 (1 failure so far) → wait 5 minutes since last attempt
        //   retry 2 (2 failures)       → wait 25 minutes since last attempt
        //   retry 3 (3 failures)       → permanent failure, never pick again
        var retry1Cutoff = now.AddMinutes(-5);
        var retry2Cutoff = now.AddMinutes(-25);

        var leadsToEnrich = await db.Leads
            .Where(l => !l.IsEnriched
                     && l.EnrichmentRetryCount < MaxRetries
                     && (l.LastEnrichmentAttempt == null
                         || (l.EnrichmentRetryCount == 1 && l.LastEnrichmentAttempt < retry1Cutoff)
                         || (l.EnrichmentRetryCount == 2 && l.LastEnrichmentAttempt < retry2Cutoff)))
            .OrderBy(l => l.CreatedAt)
            .Take(BatchSize)
            .ToListAsync();

        if (leadsToEnrich.Count == 0)
        {
            _logger.LogDebug("Enrichment sweep: no leads to enrich");
            return;
        }

        var job = new EnrichmentJob
        {
            Id = Guid.NewGuid(),
            LeadIds = leadsToEnrich.Select(l => l.Id).ToList(),
            TotalLeads = leadsToEnrich.Count,
            Status = "Queued",
            CreatedAt = now
        };

        db.EnrichmentJobs.Add(job);

        // Stamp LastEnrichmentAttempt + increment RetryCount BEFORE enqueue so crashes
        // mid-flight still count as an attempt and trigger backoff (rather than leaving
        // the lead in an eligible-forever state).
        foreach (var lead in leadsToEnrich)
        {
            lead.LastEnrichmentAttempt = now;
            lead.EnrichmentRetryCount += 1;
        }

        await db.SaveChangesAsync();

        await channel.Writer.WriteAsync(job.Id);

        _logger.LogInformation(
            "Enrichment sweep queued job {JobId} with {Count} leads",
            job.Id, leadsToEnrich.Count);
    }
}
