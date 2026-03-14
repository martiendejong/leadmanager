using LeadManager.Api.Data;
using LeadManager.Api.Hubs;
using LeadManager.Api.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Enrichment;

public class EnrichmentBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EnrichmentChannel _channel;
    private readonly IHubContext<EnrichmentHub> _hub;
    private readonly ILogger<EnrichmentBackgroundService> _logger;
    private readonly IConfiguration _configuration;

    public EnrichmentBackgroundService(
        IServiceScopeFactory scopeFactory,
        EnrichmentChannel channel,
        IHubContext<EnrichmentHub> hub,
        ILogger<EnrichmentBackgroundService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _channel = channel;
        _hub = hub;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(jobId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing enrichment job {JobId}", jobId);
            }
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();

        var job = await db.EnrichmentJobs.FindAsync(new object[] { jobId }, stoppingToken);
        if (job == null)
        {
            _logger.LogWarning("Enrichment job {JobId} not found in DB", jobId);
            return;
        }

        job.Status = "Running";
        await db.SaveChangesAsync(stoppingToken);

        var leads = await db.Leads
            .Where(l => job.LeadIds.Contains(l.Id))
            .ToListAsync(stoppingToken);

        var scraper = new ScraperService();
        var nameExtractor = new NameExtractorService(_configuration);

        var semaphore = new SemaphoreSlim(5);

        var tasks = leads.Select(async lead =>
        {
            await semaphore.WaitAsync(stoppingToken);
            try
            {
                bool isSuccess = false;
                string firstName = "";
                string lastName = "";

                try
                {
                    var text = await scraper.ScrapeOwnerTextAsync(lead.Name, lead.Website, lead.LinkedInUrl);
                    (firstName, lastName) = await nameExtractor.ExtractOwnerNameAsync(lead.Name, text);

                    if (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName))
                    {
                        lead.OwnerFirstName = firstName;
                        lead.OwnerLastName = lastName;
                        lead.OwnerName = $"{firstName} {lastName}".Trim();
                        lead.IsEnriched = true;
                        lead.EnrichedAt = DateTime.UtcNow;
                        isSuccess = true;
                    }

                    job.SuccessCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enrich lead {LeadId}", lead.Id);
                    job.ErrorCount++;
                }

                job.ProcessedLeads++;

                await _hub.Clients.Group($"job-{jobId}").SendAsync("LeadEnriched", new
                {
                    jobId = jobId.ToString(),
                    leadId = lead.Id.ToString(),
                    name = $"{firstName} {lastName}".Trim(),
                    processed = job.ProcessedLeads,
                    total = job.TotalLeads,
                    isSuccess
                }, stoppingToken);

                // Persist lead changes and job progress
                using var innerScope = _scopeFactory.CreateScope();
                var innerDb = innerScope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();

                if (isSuccess)
                {
                    var dbLead = await innerDb.Leads.FindAsync(new object[] { lead.Id }, stoppingToken);
                    if (dbLead != null)
                    {
                        dbLead.OwnerFirstName = lead.OwnerFirstName;
                        dbLead.OwnerLastName = lead.OwnerLastName;
                        dbLead.OwnerName = lead.OwnerName;
                        dbLead.IsEnriched = true;
                        dbLead.EnrichedAt = lead.EnrichedAt;
                    }
                }

                var dbJob = await innerDb.EnrichmentJobs.FindAsync(new object[] { jobId }, stoppingToken);
                if (dbJob != null)
                {
                    dbJob.ProcessedLeads = job.ProcessedLeads;
                    dbJob.SuccessCount = job.SuccessCount;
                    dbJob.ErrorCount = job.ErrorCount;
                }

                await innerDb.SaveChangesAsync(stoppingToken);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        // Mark job complete
        using var finalScope = _scopeFactory.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();
        var finalJob = await finalDb.EnrichmentJobs.FindAsync(new object[] { jobId }, stoppingToken);
        if (finalJob != null)
        {
            finalJob.Status = "Completed";
            finalJob.CompletedAt = DateTime.UtcNow;
            await finalDb.SaveChangesAsync(stoppingToken);
        }

        _logger.LogInformation("Enrichment job {JobId} completed: {Success} success, {Error} errors", jobId, job.SuccessCount, job.ErrorCount);
    }
}
