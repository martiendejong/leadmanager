using System.Text.Json;
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

        foreach (var lead in leads)
        {
            if (stoppingToken.IsCancellationRequested) break;

            bool isSuccess = false;
            string displayName = "";

            try
            {
                displayName = await EnrichLeadAsync(lead, jobId, stoppingToken);
                job.SuccessCount++;
                isSuccess = true;
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
                name = displayName,
                processed = job.ProcessedLeads,
                total = job.TotalLeads,
                isSuccess
            }, stoppingToken);
        }

        // Mark job complete
        using var finalScope = _scopeFactory.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();
        var finalJob = await finalDb.EnrichmentJobs.FindAsync(new object[] { jobId }, stoppingToken);
        if (finalJob != null)
        {
            finalJob.Status = "Completed";
            finalJob.CompletedAt = DateTime.UtcNow;
            finalJob.ProcessedLeads = job.ProcessedLeads;
            finalJob.SuccessCount = job.SuccessCount;
            finalJob.ErrorCount = job.ErrorCount;
            await finalDb.SaveChangesAsync(stoppingToken);
        }

        _logger.LogInformation("Enrichment job {JobId} completed: {Success} success, {Error} errors", jobId, job.SuccessCount, job.ErrorCount);
    }

    private async Task<string> EnrichLeadAsync(Lead lead, Guid jobId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();

        var urlNormalizer = new UrlNormalizerService();
        var sitemapService = new SitemapService();
        var pageFetcher = new PageFetcherService();
        var embeddingService = new EmbeddingService(_configuration);
        var ragService = new RagEnrichmentService(_configuration);

        // Load the seller's company profile (for sales pitch generation)
        var sellerProfile = lead.ImportedByUserId != null
            ? await db.CompanyProfiles.FirstOrDefaultAsync(p => p.UserId == lead.ImportedByUserId, stoppingToken)
            : null;

        // Step 1: Normalize URL + reachability check
        var (resolvedUrl, websiteStatus) = await urlNormalizer.NormalizeAndCheckAsync(lead.Website);

        if (websiteStatus == WebsiteStatus.Unreachable)
        {
            var dbLead = await db.Leads.FindAsync(new object[] { lead.Id }, stoppingToken);
            if (dbLead != null)
            {
                dbLead.WebsiteStatus = WebsiteStatus.Unreachable;
                dbLead.ResolvedUrl = resolvedUrl;
                dbLead.EnrichmentVersion = 2;
                dbLead.IsEnriched = true;
                dbLead.EnrichedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
            }
            return "";
        }

        // Step 2: Discover sitemap URLs
        var pageUrls = await sitemapService.DiscoverUrlsAsync(resolvedUrl, maxPages: 50);

        // Step 3: Fetch + strip page content
        // Remove existing page content for this lead (re-enrichment)
        var existingContent = db.LeadPageContents.Where(p => p.LeadId == lead.Id);
        db.LeadPageContents.RemoveRange(existingContent);
        var existingChunks = db.LeadDocumentChunks.Where(c => c.LeadId == lead.Id);
        db.LeadDocumentChunks.RemoveRange(existingChunks);
        await db.SaveChangesAsync(stoppingToken);

        var pageContents = new List<LeadPageContent>();
        foreach (var url in pageUrls)
        {
            if (stoppingToken.IsCancellationRequested) break;
            var (text, httpStatus) = await pageFetcher.FetchAndStripAsync(url);
            if (!string.IsNullOrWhiteSpace(text))
            {
                pageContents.Add(new LeadPageContent
                {
                    LeadId = lead.Id,
                    Url = url,
                    RawText = text,
                    HttpStatus = httpStatus
                });
            }
        }

        db.LeadPageContents.AddRange(pageContents);
        await db.SaveChangesAsync(stoppingToken);

        // Step 4: Chunk + embed
        var allChunks = new List<(LeadPageContent page, string chunkText, int chunkIndex)>();
        foreach (var page in pageContents)
        {
            var chunks = embeddingService.ChunkText(page.RawText);
            for (int i = 0; i < chunks.Count; i++)
                allChunks.Add((page, chunks[i], i));
        }

        if (allChunks.Count > 0)
        {
            var chunkTexts = allChunks.Select(c => c.chunkText).ToList();
            var embeddings = await embeddingService.EmbedBatchAsync(chunkTexts);

            var docChunks = allChunks.Select((c, i) => new LeadDocumentChunk
            {
                LeadId = lead.Id,
                PageContentId = c.page.Id,
                ChunkIndex = c.chunkIndex,
                ChunkText = c.chunkText,
                EmbeddingJson = i < embeddings.Count
                    ? JsonSerializer.Serialize(embeddings[i])
                    : "[]"
            }).ToList();

            db.LeadDocumentChunks.AddRange(docChunks);
            await db.SaveChangesAsync(stoppingToken);
        }

        // Step 5: RAG Q&A + sales pitch generation
        var answers = await ragService.EnrichAsync(db, lead, sellerProfile);

        // Step 6: Persist results
        var finalLead = await db.Leads.FindAsync(new object[] { lead.Id }, stoppingToken);
        if (finalLead != null)
        {
            if (!string.IsNullOrWhiteSpace(answers.OwnerName))
            {
                var parts = answers.OwnerName.Trim().Split(' ', 2);
                finalLead.OwnerFirstName = parts[0];
                finalLead.OwnerLastName = parts.Length > 1 ? parts[1] : "";
                finalLead.OwnerName = answers.OwnerName;
            }

            if (!string.IsNullOrWhiteSpace(answers.OwnerTitle))
                finalLead.OwnerTitle = answers.OwnerTitle;

            if (!string.IsNullOrWhiteSpace(answers.ContactEmail))
                finalLead.CompanyEmail = answers.ContactEmail;

            if (!string.IsNullOrWhiteSpace(answers.ContactPhone))
                finalLead.Phone = answers.ContactPhone;

            if (!string.IsNullOrWhiteSpace(answers.Description))
                finalLead.Description = answers.Description;

            if (!string.IsNullOrWhiteSpace(answers.Services))
                finalLead.Services = answers.Services;

            if (!string.IsNullOrWhiteSpace(answers.TargetAudience))
                finalLead.TargetAudience = answers.TargetAudience;

            if (!string.IsNullOrWhiteSpace(answers.AiSummary))
                finalLead.AiSummary = answers.AiSummary;

            if (!string.IsNullOrWhiteSpace(answers.SalesPitch))
                finalLead.SalesPitch = answers.SalesPitch;

            finalLead.WebsiteStatus = WebsiteStatus.Reachable;
            finalLead.ResolvedUrl = resolvedUrl;
            finalLead.CrawledAt = DateTime.UtcNow;
            finalLead.EnrichmentVersion = 2;
            finalLead.PagesCrawled = pageContents.Count;
            finalLead.ChunksIndexed = allChunks.Count;
            finalLead.IsEnriched = true;
            finalLead.EnrichedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(stoppingToken);
        }

        return finalLead?.OwnerName ?? "";
    }
}
