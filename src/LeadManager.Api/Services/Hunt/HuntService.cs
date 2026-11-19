using System.Text;
using LeadManager.Api.Data;
using LeadManager.Api.Hubs;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Hunt;

/// <summary>
/// Runs one hunt job: finds candidate companies with a real web search, looks at their own websites, verifies every score bearing
/// fact against quotes and a critic, and stores each researched company as a lead with a score that the app computes itself.
/// A verify job does the same for leads that already exist.
/// </summary>
public class HuntService
{
    public const int MaxDiscoveryRounds = 4;

    private readonly LeadManagerDbContext _db;
    private readonly LeadVerifier _verifier;
    private readonly LeadEvidenceService _evidence;
    private readonly ILeadDiscovery _discovery;
    private readonly GooglePlacesEnrichmentService _places;
    private readonly SalesScoreService _scoring;
    private readonly IHubContext<EnrichmentHub> _hub;
    private readonly ILogger<HuntService> _logger;

    public HuntService(LeadManagerDbContext db, LeadVerifier verifier, LeadEvidenceService evidence, ILeadDiscovery discovery,
        GooglePlacesEnrichmentService places, SalesScoreService scoring, IHubContext<EnrichmentHub> hub, ILogger<HuntService> logger)
    {
        _db = db; _verifier = verifier; _evidence = evidence; _discovery = discovery; _places = places; _scoring = scoring; _hub = hub; _logger = logger;
    }

    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _db.HuntJobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
        if (job == null || job.Status != HuntStatuses.Pending) return;

        job.Status = HuntStatuses.Running;
        job.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);
        await Push(job, null);

        var budget = new HuntBudget(job.MaxLlmCalls);
        try
        {
            if (job.Kind == HuntKinds.Verify) await RunVerifyAsync(job, budget, ct);
            else await RunHuntAsync(job, budget, ct);
            job.Status = job.CancelRequested ? HuntStatuses.Cancelled : HuntStatuses.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = HuntStatuses.Cancelled;
        }
        catch (HuntBudgetExceededException ex)
        {
            job.Status = HuntStatuses.Completed;
            job.Error = ex.Message + " The researched candidates are saved.";
        }
        catch (AiNotConfiguredException)
        {
            job.Status = HuntStatuses.Failed;
            job.Error = AiNotConfiguredException.Message_;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hunt job {JobId} failed", job.Id);
            job.Status = HuntStatuses.Failed;
            job.Error = "The job stopped because of an unexpected error. Check the server log.";
        }
        finally
        {
            job.LlmCalls = budget.Used;
            job.CurrentCandidate = null;
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            await Push(job, null);
        }
    }

    // ---- hunt ------------------------------------------------------------------------------------------

    private async Task RunHuntAsync(HuntJob job, HuntBudget budget, CancellationToken ct)
    {
        var existing = await _db.Leads.Where(l => l.ImportedByUserId == job.UserId).Select(l => l.Website).ToListAsync(ct);
        var seen = existing.Select(UrlGuard.DomainOf).Where(d => d != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        job.Total = job.MaxCandidates;
        var source = $"Lead hunt {DateTime.UtcNow:yyyy-MM-dd}";

        for (var round = 0; round < MaxDiscoveryRounds && job.Qualified < job.TargetCount && job.Screened < job.MaxCandidates; round++)
        {
            if (!budget.HasRoom(3) || await StopRequested(job, ct)) break;

            var wanted = Math.Min(15, Math.Max(5, (job.MaxCandidates - job.Screened) + 3));
            IReadOnlyList<DiscoveredCompany> found;
            try { found = await _discovery.DiscoverAsync(job.Segment, job.Region, seen, wanted, budget, ct); }
            catch (HttpRequestException ex)
            {
                // A refused or failed search is not rephrased to get around it: the job ends with what it has.
                job.Error = ex.Message;
                break;
            }

            var fresh = new List<DiscoveredCompany>();
            foreach (var c in found)
            {
                var domain = UrlGuard.DomainOf(c.Website);
                if (domain == "" || !seen.Add(domain)) continue;
                fresh.Add(c);
            }
            if (fresh.Count == 0) { job.Error ??= "The search found no new companies for this segment."; break; }

            foreach (var c in fresh)
            {
                if (job.Qualified >= job.TargetCount || job.Screened >= job.MaxCandidates) break;
                if (!budget.HasRoom(2) || await StopRequested(job, ct)) return;
                await ProcessCandidateAsync(job, c, source, budget, ct);
            }
        }
    }

    private async Task ProcessCandidateAsync(HuntJob job, DiscoveredCompany c, string source, HuntBudget budget, CancellationToken ct)
    {
        job.Screened++;
        job.CurrentCandidate = c.Name;
        await _db.SaveChangesAsync(CancellationToken.None);
        await Push(job, null);

        var cand = new HuntCandidate { JobId = job.Id, Name = c.Name, Website = c.Website };
        try
        {
            var site = await _verifier.QuickLookAsync(c.Website, ct);
            if (!site.Reachable)
            {
                cand.Outcome = HuntOutcomes.Unreachable;
                cand.Reason = "The website did not answer, so nothing could be checked. Not added as a lead.";
            }
            else
            {
                var quick = _evidence.Extract(site.Pages, site.Home!.Url);
                var max = LeadVerifier.MaxReachable(quick, _places.IsConfigured);
                cand.MaxReachable = max;
                var bar = job.Ratchet ? Math.Max(job.MinScore, job.BestScore) : job.MinScore;

                var lead = new Lead
                {
                    Name = c.Name, Website = c.Website, City = c.City, Sector = c.Sector, Source = source,
                    ImportedByUserId = job.UserId, ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
                };

                if (max < bar)
                {
                    cand.Outcome = HuntOutcomes.StoppedEarly;
                    cand.Reason = $"Stopped after the quick look: the score can be at most {max}, the bar is {bar}. Kept as a lead without score, not researched further.";
                    cand.EvidenceJson = LeadVerifier.BuildEvidenceJson(new VerificationResult { Website = c.Website, Reachable = true, Facts = quick }, max);
                    lead.WebsiteStatus = WebsiteStatus.Reachable;
                    await SaveLeadAsync(job, cand, lead, $"{source}. Stopped after a quick look at {site.Pages.Count} page(s): the score can be at most {max}, the bar was {bar}. Not verified, no score.", ct);
                }
                else
                {
                    await _verifier.DeepenAsync(site, ct);
                    var result = await _verifier.VerifyAsync(site, c.Name, budget, ct);
                    LeadVerifier.Apply(lead, result, clearUnverified: false, _scoring);
                    await ApplyGoogleAsync(lead, result, ct);
                    var score = lead.SalesPriorityScore ?? 0;
                    cand.Score = score;
                    cand.Outcome = score >= job.MinScore ? HuntOutcomes.Qualified : HuntOutcomes.BelowMinimum;
                    cand.Reason = score >= job.MinScore
                        ? $"Verified score {score} reaches the minimum of {job.MinScore}."
                        : $"Verified score {score} is below the minimum of {job.MinScore}.";
                    cand.EvidenceJson = LeadVerifier.BuildEvidenceJson(result, max);
                    var note = LeadVerifier.BuildNote(source, score, result, Array.Empty<string>());
                    if (await SaveLeadAsync(job, cand, lead, note, ct))
                    {
                        if (score >= job.MinScore) job.Qualified++;
                        job.BestScore = Math.Max(job.BestScore, score);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HuntBudgetExceededException) { await Record(job, cand, ct); throw; }
        catch (Exception ex) when (ex is not AiNotConfiguredException)
        {
            _logger.LogWarning(ex, "Candidate {Name} failed", c.Name);
            cand.Outcome = HuntOutcomes.Error;
            cand.Reason = "Checking this company failed: " + (ex is HttpRequestException ? ex.Message : "unexpected error.");
        }
        await Record(job, cand, ct);
    }

    /// <summary>Saves the lead and its evidence note; false when it already exists (same name and site for this user).</summary>
    private async Task<bool> SaveLeadAsync(HuntJob job, HuntCandidate cand, Lead lead, string note, CancellationToken ct)
    {
        var noteRow = new LeadNote { LeadId = lead.Id, Content = note, CreatedByUserId = job.UserId ?? "" };
        _db.Leads.Add(lead);
        _db.LeadNotes.Add(noteRow);
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
            cand.LeadId = lead.Id;
            return true;
        }
        catch (DbUpdateException)
        {
            _db.Entry(noteRow).State = EntityState.Detached;
            _db.Entry(lead).State = EntityState.Detached;
            cand.Score = null;
            var duplicate = await _db.Leads.AnyAsync(l => l.ImportedByUserId == job.UserId && l.Name == lead.Name && l.Website == lead.Website, CancellationToken.None);
            cand.Outcome = duplicate ? HuntOutcomes.Duplicate : HuntOutcomes.Error;
            cand.Reason = duplicate ? "A lead with this name and website already exists." : "The lead could not be saved.";
            return false;
        }
    }

    private async Task Record(HuntJob job, HuntCandidate cand, CancellationToken ct)
    {
        _db.HuntCandidates.Add(cand);
        await _db.SaveChangesAsync(CancellationToken.None);
        await Push(job, cand);
    }

    // ---- verify ----------------------------------------------------------------------------------------

    private async Task RunVerifyAsync(HuntJob job, HuntBudget budget, CancellationToken ct)
    {
        job.Total = job.LeadIds.Count;
        foreach (var id in job.LeadIds)
        {
            if (await StopRequested(job, ct)) return;
            if (!budget.HasRoom(2)) throw new HuntBudgetExceededException(budget.Max);

            var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == job.UserId, ct);
            if (lead == null) continue;

            job.Screened++;
            job.CurrentCandidate = lead.Name;
            await _db.SaveChangesAsync(CancellationToken.None);
            await Push(job, null);

            var cand = new HuntCandidate { JobId = job.Id, LeadId = lead.Id, Name = lead.Name, Website = lead.Website };
            try
            {
                var site = await _verifier.QuickLookAsync(lead.Website, ct);
                if (!site.Reachable)
                {
                    lead.WebsiteStatus = WebsiteStatus.Unreachable;
                    var s0 = _scoring.CalculateScore(lead);
                    lead.SalesPriorityScore = s0.Score; lead.SalesPriorityLabel = s0.Label; lead.SalesPriorityReasoning = s0.Reasoning;
                    cand.Outcome = HuntOutcomes.Unreachable;
                    cand.Score = lead.SalesPriorityScore;
                    cand.Reason = "The website did not answer, so nothing could be verified. Existing values were left as they are.";
                    _db.LeadNotes.Add(new LeadNote { LeadId = lead.Id, CreatedByUserId = job.UserId ?? "", Content = "Verify: the website did not answer, nothing could be verified. Values were left unchanged." });
                }
                else
                {
                    await _verifier.DeepenAsync(site, ct);
                    var result = await _verifier.VerifyAsync(site, lead.Name, budget, ct);
                    if (!result.LlmRan)
                    {
                        cand.Outcome = HuntOutcomes.Error;
                        cand.Reason = result.Dropped.FirstOrDefault()?.Reason ?? "The verification could not run. Existing values were left unchanged.";
                        cand.EvidenceJson = LeadVerifier.BuildEvidenceJson(result, null);
                    }
                    else
                    {
                        var removed = LeadVerifier.Apply(lead, result, clearUnverified: true, _scoring);
                        await ApplyGoogleAsync(lead, result, ct, existingLead: true, removed);
                        cand.Score = lead.SalesPriorityScore;
                        cand.Outcome = HuntOutcomes.Verified;
                        cand.Reason = $"Verified and rescored: {lead.SalesPriorityScore}.";
                        cand.EvidenceJson = LeadVerifier.BuildEvidenceJson(result, null);
                        _db.LeadNotes.Add(new LeadNote { LeadId = lead.Id, CreatedByUserId = job.UserId ?? "", Content = LeadVerifier.BuildNote("Verify and rescore " + DateTime.UtcNow.ToString("yyyy-MM-dd"), lead.SalesPriorityScore ?? 0, result, removed) });
                        job.Qualified++;
                        job.BestScore = Math.Max(job.BestScore, lead.SalesPriorityScore ?? 0);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HuntBudgetExceededException) { throw; }
            catch (Exception ex) when (ex is not AiNotConfiguredException)
            {
                _logger.LogWarning(ex, "Verify of lead {LeadId} failed", lead.Id);
                cand.Outcome = HuntOutcomes.Error;
                cand.Reason = "Verifying this lead failed: " + (ex is HttpRequestException ? ex.Message : "unexpected error.");
            }
            await Record(job, cand, ct);
        }
    }

    // ---- google ----------------------------------------------------------------------------------------

    /// <summary>
    /// The rating comes from the Google Places listing only, and only when the listing is clearly this business. For an existing lead,
    /// a rating that did not come from Google Places is removed, because a widget or a review site is not Google.
    /// </summary>
    private async Task ApplyGoogleAsync(Lead lead, VerificationResult result, CancellationToken ct, bool existingLead = false, List<string>? removed = null)
    {
        GooglePlacesResult? g = null;
        if (_places.IsConfigured)
        {
            g = await _places.EnrichAsync(lead.Name, string.IsNullOrWhiteSpace(lead.City) ? null : lead.City);
            if (g?.GoogleRating != null && NameMatches(g.DisplayName, lead.Name))
            {
                lead.GoogleRating = g.GoogleRating;
                lead.GoogleReviewCount = g.GoogleReviewCount;
                lead.GoogleMapsUrl = g.GoogleMapsUrl;
                result.GoogleNote = $"{g.GoogleRating:F1} from {g.GoogleReviewCount} reviews on the Google listing '{g.DisplayName}'.";
            }
            else
            {
                result.GoogleNote = g == null ? "No Google listing found." : $"The Google listing '{g.DisplayName}' does not clearly match this business, so no rating is used.";
                g = null;
            }
        }
        else result.GoogleNote = "Google Places is not configured on this server, so no Google rating could be checked.";

        if (g == null && existingLead && lead.GoogleRating != null && string.IsNullOrWhiteSpace(lead.GoogleMapsUrl))
        {
            removed?.Add($"googleRating {lead.GoogleRating} (it is not from a Google listing)");
            lead.GoogleRating = null;
            lead.GoogleReviewCount = null;
        }
        var s = _scoring.CalculateScore(lead);
        lead.SalesPriorityScore = s.Score; lead.SalesPriorityLabel = s.Label; lead.SalesPriorityReasoning = s.Reasoning;
    }

    public static bool NameMatches(string? listed, string company)
    {
        if (string.IsNullOrWhiteSpace(listed)) return false;
        var l = LeadEvidenceService.Fold(listed);
        var tokens = company.Split(new[] { ' ', '-', '&', '/', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(LeadEvidenceService.Fold).Where(t => t.Length >= 4 && t is not ("advies" or "adviseur" or "bureau" or "praktijk" or "studio" or "kantoor" or "groep" or "nederland")).ToList();
        return tokens.Count > 0 && tokens.Any(t => l.Contains(t));
    }

    // ---- progress --------------------------------------------------------------------------------------

    private async Task<bool> StopRequested(HuntJob job, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return true;
        var flag = await _db.HuntJobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.CancelRequested).FirstOrDefaultAsync(CancellationToken.None);
        if (flag) job.CancelRequested = true;
        return flag;
    }

    private async Task Push(HuntJob job, HuntCandidate? cand)
    {
        try
        {
            await _hub.Clients.Group($"job-{job.Id}").SendAsync("HuntProgress", new
            {
                jobId = job.Id,
                kind = job.Kind,
                status = job.Status,
                screened = job.Screened,
                total = job.Total,
                qualified = job.Qualified,
                best = job.BestScore,
                current = job.CurrentCandidate,
                error = job.Error,
                candidate = cand == null ? null : new { id = cand.Id, name = cand.Name, website = cand.Website, score = cand.Score, maxReachable = cand.MaxReachable, outcome = cand.Outcome, reason = cand.Reason, leadId = cand.LeadId },
            });
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Progress push failed"); }
    }
}
