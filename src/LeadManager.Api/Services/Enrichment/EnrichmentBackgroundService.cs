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
        var webSearchLogger = scope.ServiceProvider.GetRequiredService<ILogger<WebSearchEnrichmentService>>();
        var webSearchService = new WebSearchEnrichmentService(webSearchLogger);
        var kvkLogger = scope.ServiceProvider.GetRequiredService<ILogger<KvkEnrichmentService>>();
        var kvkService = new KvkEnrichmentService(kvkLogger);
        var googleLogger = scope.ServiceProvider.GetRequiredService<ILogger<GooglePlacesEnrichmentService>>();
        var googleService = new GooglePlacesEnrichmentService(_configuration, googleLogger);
        var salesScoreService = new SalesScoreService();
        var textInputLogger = scope.ServiceProvider.GetRequiredService<ILogger<TextInputEnrichmentService>>();
        var textInputService = new TextInputEnrichmentService(_configuration, textInputLogger);
        var salesApproachLogger = scope.ServiceProvider.GetRequiredService<ILogger<AiSalesApproachService>>();
        var salesApproachService = new AiSalesApproachService(_configuration, salesApproachLogger);

        // Step 0a: Text Input Enrichment (Task #3 - enrich from manual text if provided)
        TextEnrichmentResult? textResult = null;
        if (!string.IsNullOrWhiteSpace(lead.ManualInput))
        {
            try
            {
                textResult = await textInputService.EnrichFromTextAsync(lead);
                if (textResult != null)
                {
                    _logger.LogInformation("Text input enrichment extracted data for lead {LeadId}", lead.Id);

                    // Apply text enrichment results to lead immediately
                    var dbLead = await db.Leads.FindAsync(new object[] { lead.Id }, stoppingToken);
                    if (dbLead != null)
                    {
                        if (!string.IsNullOrWhiteSpace(textResult.OwnerName))
                            dbLead.OwnerName = textResult.OwnerName;
                        if (!string.IsNullOrWhiteSpace(textResult.Email))
                            dbLead.PersonalEmail = textResult.Email;
                        if (!string.IsNullOrWhiteSpace(textResult.Phone))
                            dbLead.Phone = textResult.Phone;
                        if (!string.IsNullOrWhiteSpace(textResult.City))
                            dbLead.City = textResult.City;
                        if (!string.IsNullOrWhiteSpace(textResult.Sector))
                            dbLead.Sector = textResult.Sector;
                        if (!string.IsNullOrWhiteSpace(textResult.Website))
                            dbLead.Website = textResult.Website;

                        await db.SaveChangesAsync(stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Text input enrichment failed for lead {LeadId}, continuing", lead.Id);
            }
        }

        // Step 0b: Web Search Enrichment (discover additional info via search engines)
        try
        {
            var searchResult = await webSearchService.EnrichLeadAsync(lead, stoppingToken);
            if (searchResult.Success && searchResult.TotalResults > 0)
            {
                _logger.LogInformation(
                    "WebSearch found {ResultCount} results for lead {LeadId} ({CompanyName})",
                    searchResult.TotalResults, lead.Id, lead.Name
                );

                // Extract LinkedIn URL if found
                var linkedInResult = searchResult.SearchResults.FirstOrDefault(r =>
                    r.Url.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase));

                if (linkedInResult != null && string.IsNullOrWhiteSpace(lead.LinkedInUrl))
                {
                    var dbLead = await db.Leads.FindAsync(new object[] { lead.Id }, stoppingToken);
                    if (dbLead != null)
                    {
                        dbLead.LinkedInUrl = linkedInResult.Url;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                }

                // TODO: Store search results in a dedicated table for later analysis
                // Could create LeadWebSearchResults table to persist all findings
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSearch enrichment failed for lead {LeadId}, continuing with RAG enrichment", lead.Id);
        }

        // Load the seller's company profile (for sales pitch generation)
        var sellerProfile = lead.ImportedByUserId != null
            ? await db.CompanyProfiles.FirstOrDefaultAsync(p => p.UserId == lead.ImportedByUserId, stoppingToken)
            : null;

        // Task #4: Skip website enrichment if no website provided
        if (string.IsNullOrWhiteSpace(lead.Website))
        {
            // No website - skip to text/document enrichment only
            _logger.LogInformation("Lead {LeadId} has no website URL - skipping website enrichment", lead.Id);

            // Mark as enriched (from text/documents only)
            var dbLead = await db.Leads.FindAsync(new object[] { lead.Id }, stoppingToken);
            if (dbLead != null)
            {
                dbLead.WebsiteStatus = WebsiteStatus.Unknown;
                dbLead.IsEnriched = true;
                dbLead.EnrichedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
            }

            return lead.OwnerName;
        }

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

        // Step 5a: KvK enrichment
        KvkEnrichmentResult? kvkResult = null;
        try
        {
            kvkResult = await kvkService.EnrichAsync(lead.Name, lead.City);
            if (kvkResult != null)
            {
                _logger.LogInformation("KvK enrichment successful for lead {LeadId}: KvK#{KvkNumber}", lead.Id, kvkResult.KvkNumber);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KvK enrichment failed for lead {LeadId}, continuing", lead.Id);
        }

        // Step 5b: Google Places enrichment
        GooglePlacesResult? googleResult = null;
        try
        {
            googleResult = await googleService.EnrichAsync(lead.Name, lead.City);
            if (googleResult != null)
            {
                _logger.LogInformation("Google Places enrichment successful for lead {LeadId}: {Rating}/5 ({ReviewCount} reviews)",
                    lead.Id, googleResult.GoogleRating, googleResult.GoogleReviewCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Places enrichment failed for lead {LeadId}, continuing", lead.Id);
        }

        // Step 5c: AI Sales Approach generation (Task #7)
        SalesApproachResult? salesApproachResult = null;
        try
        {
            salesApproachResult = await salesApproachService.GenerateAsync(lead);
            if (salesApproachResult != null)
            {
                _logger.LogInformation("AI sales approach generated for lead {LeadId}", lead.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI sales approach generation failed for lead {LeadId}, continuing", lead.Id);
        }

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

            // Persist KvK enrichment results
            if (kvkResult != null)
            {
                finalLead.KvkNumber = kvkResult.KvkNumber;
                finalLead.VatNumber = kvkResult.VatNumber;
                finalLead.Street = kvkResult.Street;
                finalLead.ZipCode = kvkResult.ZipCode;
                finalLead.EmployeeCount = kvkResult.EmployeeCount;
                finalLead.FoundingYear = kvkResult.FoundingYear;
                finalLead.LegalForm = kvkResult.LegalForm;
            }

            // Persist Google Places enrichment results
            if (googleResult != null)
            {
                finalLead.GoogleRating = googleResult.GoogleRating;
                finalLead.GoogleReviewCount = googleResult.GoogleReviewCount;
                finalLead.GoogleMapsUrl = googleResult.GoogleMapsUrl;
            }

            // Persist AI sales approach (Task #7)
            if (salesApproachResult != null)
            {
                finalLead.SalesApproach = JsonSerializer.Serialize(salesApproachResult);
            }

            // Calculate and save sales priority score
            finalLead.SalesPriorityScore = salesScoreService.CalculateScore(finalLead);

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
