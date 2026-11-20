using System.Text;
using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Hubs;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Hunt;

/// <summary>
/// The server side of the lead hunt. A Jengo agent does the research on the web and submits candidates one by one; this service does not
/// believe them: it fetches the candidate's own pages itself, accepts only the score bearing claims whose quotes occur on those pages
/// and that pass the rule for their field, and lets SalesScoreService compute the score. It also runs verify jobs for existing leads,
/// which need no agent: the same rules, applied to the lead's own website.
/// </summary>
public class HuntService
{
    private readonly LeadManagerDbContext _db;
    private readonly LeadVerifier _verifier;
    private readonly GooglePlacesEnrichmentService _places;
    private readonly SalesScoreService _scoring;
    private readonly IHubContext<EnrichmentHub> _hub;
    private readonly ILogger<HuntService> _logger;

    public HuntService(LeadManagerDbContext db, LeadVerifier verifier, GooglePlacesEnrichmentService places, SalesScoreService scoring,
        IHubContext<EnrichmentHub> hub, ILogger<HuntService> logger)
    {
        _db = db; _verifier = verifier; _places = places; _scoring = scoring; _hub = hub; _logger = logger;
    }

    /// <summary>Runs a verify job (leads that already exist). Hunt jobs are driven by the agent through <see cref="SubmitCandidateAsync"/>.</summary>
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _db.HuntJobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
        if (job == null || job.Status != HuntStatuses.Pending || job.Kind != HuntKinds.Verify) return;

        job.Status = HuntStatuses.Running;
        job.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);
        await Push(job, null);

        try
        {
            await RunVerifyAsync(job, ct);
            job.Status = job.CancelRequested ? HuntStatuses.Cancelled : HuntStatuses.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = HuntStatuses.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Verify job {JobId} failed", job.Id);
            job.Status = HuntStatuses.Failed;
            job.Error = "The job stopped because of an unexpected error. Check the server log.";
        }
        finally
        {
            job.CurrentCandidate = null;
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            await Push(job, null);
        }
    }

    // ---- hunt: candidates submitted by the agent ---------------------------------------------------------

    public record Submission(string Name, string Website, string? City, string? Sector, JsonElement Claims, int? ClaimedScore, string? StopReason);

    public record SubmissionResult(string Outcome, int? Score, string Reason, Guid? LeadId, bool Duplicate, int BestScoreSoFar, int Qualified, int Screened, bool Stop);

    public const int MaxNameLength = 160;

    /// <summary>The domains of every lead of this user, so the agent does not research a company that is already known.</summary>
    public async Task<List<string>> KnownDomainsAsync(string? userId, int max = 1500)
    {
        var sites = await _db.Leads.AsNoTracking().Where(l => l.ImportedByUserId == userId).Select(l => l.Website).ToListAsync();
        return sites.Select(UrlGuard.DomainOf).Where(d => d != "").Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>One candidate (all its pages, the verification and the save) may take this long.</summary>
    public static readonly TimeSpan CandidateBudget = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Verifies one submitted candidate. The caller holds the lock of the job (<see cref="HuntLocks.TryEnterJob"/>), so counters are never
    /// updated by two submissions at once; the stop conditions are checked again after the slow part, before anything is saved.
    /// </summary>
    public async Task<SubmissionResult> SubmitCandidateAsync(HuntJob job, Submission sub, CancellationToken ct)
    {
        Touch(job);
        await _db.SaveChangesAsync(CancellationToken.None);
        SubmissionResult Done(string outcome, string reason, int? score = null, Guid? leadId = null, bool dup = false) =>
            new(outcome, score, reason, leadId, dup, job.BestScore, job.Qualified, job.Screened, ShouldStop(job));

        if (ShouldStop(job)) return Done(HuntOutcomes.Rejected, job.CancelRequested ? "The hunt was cancelled." : "The hunt already reached its limit.");

        var name = TextSafety.Clean(sub.Name, MaxNameLength, dropOrders: false);   // a company name is a label
        if (name.Length < 2) return Done(HuntOutcomes.Rejected, "The company name is missing or too long.");
        if (!UrlGuard.TryNormalize(sub.Website ?? "", out var uri)) return Done(HuntOutcomes.Rejected, "The website is not a public http(s) address.");
        var website = UrlGuard.CanonicalWebsite(uri);   // keeps the path of a site that is shared by path (linktr.ee/name)
        var domain = UrlGuard.DomainOf(website);

        job.CurrentCandidate = name;
        await _db.SaveChangesAsync(CancellationToken.None);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CandidateBudget);
        var cand = new HuntCandidate { JobId = job.Id, Name = name, Website = website };
        Lead? lead = null;
        string? note = null;
        var matched = false;
        var source = $"Lead hunt {DateTime.UtcNow:yyyy-MM-dd}";
        try
        {
            var knownDomains = await KnownDomainsAsync(job.UserId, int.MaxValue);
            if (knownDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                cand.Outcome = HuntOutcomes.Duplicate;
                cand.Reason = "This company is already one of your leads. Skipped.";
            }
            else
            {
                var site = await _verifier.QuickLookAsync(website, budget.Token);
                if (!site.Reachable)
                {
                    cand.Outcome = HuntOutcomes.Unreachable;
                    cand.Reason = "The website did not answer, so nothing could be checked. Not added as a lead.";
                }
                else
                {
                    await _verifier.DeepenAsync(site, budget.Token);
                    var result = _verifier.VerifyClaims(site, sub.Claims);

                    lead = new Lead
                    {
                        Name = name, Website = website, City = TextSafety.Clean(sub.City, 60, dropOrders: false), Sector = TextSafety.Clean(sub.Sector, 80, dropOrders: false), Source = source,
                        ImportedByUserId = job.UserId, ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
                    };
                    LeadVerifier.Apply(lead, result, clearUnverified: false, _scoring);
                    await ApplyGoogleAsync(lead, result, budget.Token);
                    LeadInsight.Apply(lead, result);   // why, needs and product fit: the verified parts only
                    var score = lead.SalesPriorityScore ?? 0;
                    cand.Score = score;
                    cand.MaxReachable = LeadVerifier.MaxReachable(result.Facts, _places.IsConfigured);

                    matched = score >= job.MinScore;
                    // The ratchet, enforced here and not left to the agent: a candidate that cannot beat the best score so far, even with every
                    // point it has not been proven to lack, is not worth more research. It is still saved as a lead.
                    var cannotBeat = !matched && job.Ratchet && job.BestScore > 0 && cand.MaxReachable is int mr && mr <= job.BestScore;
                    cand.Outcome = matched ? HuntOutcomes.Qualified
                        : !string.IsNullOrWhiteSpace(sub.StopReason) || cannotBeat ? HuntOutcomes.StoppedEarly : HuntOutcomes.BelowMinimum;
                    var claimedNote = sub.ClaimedScore is int c && c != score ? $" The agent claimed {c}; the server verified {score}." : "";
                    cand.Reason = (matched
                        ? $"Verified score {score} reaches the minimum of {job.MinScore}."
                        : !string.IsNullOrWhiteSpace(sub.StopReason) ? $"Verified score {score}. The agent stopped researching: {TextSafety.Clean(sub.StopReason, 300)}"
                        : cannotBeat ? $"Verified score {score}. Even with every missing point it cannot beat the best score so far ({job.BestScore}), so it was not researched further."
                        : $"Verified score {score} is below the minimum of {job.MinScore}.") + claimedNote;
                    cand.EvidenceJson = LeadVerifier.BuildEvidenceJson(result, cand.MaxReachable);
                    note = LeadVerifier.BuildNote(source, score, result, Array.Empty<string>()) + claimedNote;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            lead = null;
            cand.Outcome = HuntOutcomes.Error;
            cand.Reason = $"Checking this company took longer than {(int)CandidateBudget.TotalSeconds} seconds. It was skipped and not saved.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Candidate {Name} failed", name);
            lead = null;
            cand.Outcome = HuntOutcomes.Error;
            cand.Reason = "Checking this company failed: " + (ex is HttpRequestException ? ex.Message : "unexpected error.");
        }

        // The hunt may have been cancelled, finished or have reached its limit while the pages were fetched: nothing is saved then.
        await _db.Entry(job).ReloadAsync(CancellationToken.None);
        if (ShouldStop(job))
            return Done(HuntOutcomes.Rejected, job.CancelRequested ? "The hunt was cancelled while this company was being checked. It was not saved." : "The hunt reached its limit while this company was being checked. It was not saved.");

        job.Screened++;
        if (lead != null && note != null && await SaveLeadAsync(job, cand, lead, note))
        {
            if (matched) job.Qualified++;
            job.BestScore = Math.Max(job.BestScore, cand.Score ?? 0);
        }
        await Record(job, cand);
        return Done(cand.Outcome, cand.Reason, cand.Score, cand.LeadId, cand.Outcome == HuntOutcomes.Duplicate);
    }

    /// <summary>
    /// A review job: the agent researched one of the user's existing leads and submits what it found. The server fetches that lead's own website
    /// itself and applies only the verified facts: nothing a person entered is overwritten (it is listed in the note instead), nothing is cleared.
    /// </summary>
    public async Task<SubmissionResult> SubmitReviewAsync(HuntJob job, Guid leadId, JsonElement claims, int? claimedScore, CancellationToken ct)
    {
        Touch(job);
        await _db.SaveChangesAsync(CancellationToken.None);
        SubmissionResult Done(string outcome, string reason, int? score = null, Guid? id = null, bool dup = false) =>
            new(outcome, score, reason, id, dup, job.BestScore, job.Qualified, job.Screened, ShouldStop(job));

        if (ShouldStop(job)) return Done(HuntOutcomes.Rejected, job.CancelRequested ? "The review was cancelled." : "The review already covered all its leads.");
        if (!job.LeadIds.Contains(leadId)) return Done(HuntOutcomes.Rejected, "This lead is not part of this review.");
        if (await _db.HuntCandidates.AnyAsync(c => c.JobId == job.Id && c.LeadId == leadId, CancellationToken.None))
            return Done(HuntOutcomes.Duplicate, "This lead was already reviewed in this run.", dup: true);
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId && l.ImportedByUserId == job.UserId, CancellationToken.None);
        if (lead == null) return Done(HuntOutcomes.Rejected, "This lead does not exist.");

        job.CurrentCandidate = lead.Name;
        await _db.SaveChangesAsync(CancellationToken.None);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CandidateBudget);
        var cand = new HuntCandidate { JobId = job.Id, LeadId = lead.Id, Name = lead.Name, Website = lead.Website };
        try
        {
            var site = await _verifier.QuickLookAsync(lead.Website, budget.Token);
            if (!site.Reachable)
            {
                cand.Outcome = HuntOutcomes.Unreachable;
                cand.Reason = "The website did not answer, so nothing could be checked. The lead was left as it is.";
                cand.Score = lead.SalesPriorityScore;
            }
            else
            {
                await _verifier.DeepenAsync(site, budget.Token);
                var result = _verifier.VerifyClaims(site, claims);
                var kept = new List<string>();
                var removed = LeadVerifier.Apply(lead, result, clearUnverified: false, _scoring, kept);
                await ApplyGoogleAsync(lead, result, budget.Token, existingLead: true, removed, clear: false, kept);
                LeadInsight.Apply(lead, result);
                var score = lead.SalesPriorityScore ?? 0;
                var claimedNote = claimedScore is int c && c != score ? $" The agent claimed {c}; the server verified {score}." : "";
                cand.Score = score;
                cand.MaxReachable = LeadVerifier.MaxReachable(result.Facts, _places.IsConfigured);
                cand.Outcome = HuntOutcomes.Verified;
                cand.Reason = $"Reviewed by the agent and verified by the server: score {score}." + claimedNote;
                cand.EvidenceJson = LeadVerifier.BuildEvidenceJson(result, cand.MaxReachable);
                _db.LeadNotes.Add(new LeadNote { LeadId = lead.Id, CreatedByUserId = job.UserId ?? "",
                    Content = LeadVerifier.BuildNote("Agent review " + DateTime.UtcNow.ToString("yyyy-MM-dd"), score, result, removed) + KeptNote(kept) + claimedNote });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Review of lead {LeadId} failed", lead.Id);
            _db.ChangeTracker.Clear();
            cand = new HuntCandidate { JobId = job.Id, LeadId = leadId, Name = lead.Name, Website = lead.Website, Outcome = HuntOutcomes.Error,
                Reason = ex is OperationCanceledException ? $"Checking this lead took longer than {(int)CandidateBudget.TotalSeconds} seconds. It was left as it is."
                    : "Checking this lead failed: " + (ex is HttpRequestException ? ex.Message : "unexpected error.") };
            job = await _db.HuntJobs.FirstAsync(j => j.Id == job.Id, CancellationToken.None);
        }

        await _db.Entry(job).ReloadAsync(CancellationToken.None);
        job.Screened++;
        if (cand.Outcome == HuntOutcomes.Verified) { job.Qualified++; job.BestScore = Math.Max(job.BestScore, cand.Score ?? 0); }
        await Record(job, cand);
        return Done(cand.Outcome, cand.Reason, cand.Score, cand.LeadId);
    }

    /// <summary>True when the agent must stop: cancelled, enough matches, or enough candidates screened.</summary>
    public static bool ShouldStop(HuntJob job) =>
        job.CancelRequested || job.Status is HuntStatuses.Cancelled or HuntStatuses.Completed or HuntStatuses.Failed
        || job.Qualified >= job.TargetCount || job.Screened >= job.MaxCandidates;

    /// <summary>The agent called: the job is running and alive.</summary>
    public static void Touch(HuntJob job)
    {
        job.LastAgentCallAt = DateTime.UtcNow;
        if (job.Status == HuntStatuses.Pending) { job.Status = HuntStatuses.Running; job.StartedAt ??= DateTime.UtcNow; }
    }

    public async Task CompleteAsync(HuntJob job, string? summary, bool failed)
    {
        Touch(job);
        job.Summary = Clip(summary, 1500);
        if (failed) { job.Status = HuntStatuses.Failed; job.Error = Clip(summary, 500) ?? "The agent reported a failure."; }
        else job.Status = job.CancelRequested ? HuntStatuses.Cancelled : HuntStatuses.Completed;
        job.CurrentCandidate = null;
        job.CompletedAt = DateTime.UtcNow;
        HuntTokens.Revoke(job);
        await _db.SaveChangesAsync(CancellationToken.None);
        HuntLocks.Forget(job.Id);
        await Push(job, null);
    }

    /// <summary>The agent says what it is working on. Only the display text is taken from it; the counters are the server's.</summary>
    public async Task ProgressAsync(HuntJob job, string? current)
    {
        Touch(job);
        var text = TextSafety.Clean(current, MaxNameLength, dropOrders: false);
        job.CurrentCandidate = text.Length == 0 ? null : text;
        await _db.SaveChangesAsync(CancellationToken.None);
        await Push(job, null);
    }

    /// <summary>Hunts whose agent has been silent for too long are marked failed, so the screen never shows them as running forever.</summary>
    public static async Task<int> SweepStaleAsync(LeadManagerDbContext db, DateTime now, TimeSpan silence)
    {
        var limit = now - silence;
        var stale = await db.HuntJobs.Where(j => (j.Kind == HuntKinds.Hunt || j.Kind == HuntKinds.Review) && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running)
            && (j.LastAgentCallAt ?? j.CreatedAt) < limit).ToListAsync();
        foreach (var j in stale)
        {
            j.Status = HuntStatuses.Failed;
            j.Error = $"The Jengo agent did not report in {(int)silence.TotalMinutes} minutes. The candidates that were finished are saved.";
            j.CompletedAt = now;
            HuntTokens.Revoke(j);
            HuntLocks.Forget(j.Id);
        }
        if (stale.Count > 0) await db.SaveChangesAsync();
        return stale.Count;
    }

    private static string? Clip(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return s;
        s = new string(s.Where(ch => !char.IsControl(ch)).ToArray());
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>Saves the lead and its evidence note; false when it already exists (same name and site for this user).</summary>
    private async Task<bool> SaveLeadAsync(HuntJob job, HuntCandidate cand, Lead lead, string note)
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

    private async Task Record(HuntJob job, HuntCandidate cand)
    {
        _db.HuntCandidates.Add(cand);
        await _db.SaveChangesAsync(CancellationToken.None);
        await Push(job, cand);
    }

    // ---- verify ----------------------------------------------------------------------------------------

    private async Task RunVerifyAsync(HuntJob job, CancellationToken ct)
    {
        job.Total = job.LeadIds.Count;
        foreach (var id in job.LeadIds)
        {
            if (await StopRequested(job, ct)) return;

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
                    var result = _verifier.VerifyDeterministic(site, lead.OwnerName);
                    // Values that a person typed stay unless the person confirmed the removal; values a hunt or an enrichment wrote may go.
                    var mayClear = MayClear(lead, job.ConfirmClear);
                    var kept = new List<string>();
                    var removed = LeadVerifier.Apply(lead, result, clearUnverified: true, _scoring, kept, mayClear);
                    await ApplyGoogleAsync(lead, result, ct, existingLead: true, removed, mayClear("googleRating"), kept);
                    cand.Score = lead.SalesPriorityScore;
                    cand.Outcome = HuntOutcomes.Verified;
                    cand.Reason = $"Verified and rescored: {lead.SalesPriorityScore}.";
                    cand.EvidenceJson = LeadVerifier.BuildEvidenceJson(result, null);
                    _db.LeadNotes.Add(new LeadNote { LeadId = lead.Id, CreatedByUserId = job.UserId ?? "", Content = LeadVerifier.BuildNote("Verify and rescore " + DateTime.UtcNow.ToString("yyyy-MM-dd"), lead.SalesPriorityScore ?? 0, result, removed) + KeptNote(kept) });
                    job.Qualified++;
                    job.BestScore = Math.Max(job.BestScore, lead.SalesPriorityScore ?? 0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Verify of lead {LeadId} failed", lead.Id);
                cand.Outcome = HuntOutcomes.Error;
                cand.Reason = "Verifying this lead failed: " + (ex is HttpRequestException ? ex.Message : "unexpected error.");
            }
            await Record(job, cand);
        }
    }

    // ---- google ----------------------------------------------------------------------------------------

    /// <summary>
    /// The rating comes from the Google Places listing only, and only when the listing is clearly this business. For an existing lead,
    /// a rating that did not come from Google Places is removed, because a widget or a review site is not Google.
    /// </summary>
    private async Task ApplyGoogleAsync(Lead lead, VerificationResult result, CancellationToken ct, bool existingLead = false, List<string>? removed = null, bool clear = true, List<string>? kept = null)
    {
        GooglePlacesResult? g = null;
        if (_places.IsConfigured)
        {
            g = await _places.EnrichAsync(lead.Name, string.IsNullOrWhiteSpace(lead.City) ? null : lead.City);
            if (g?.GoogleRating != null && ListingIsThisBusiness(g, lead, result.Facts))
            {
                var existing = lead.GoogleRating;
                var differs = existing == null || Math.Abs(existing.Value - g.GoogleRating.Value) > 0.001f;
                if (existing != null && differs && !clear)
                {
                    kept?.Add($"googleRating {existing} differs from the Google listing ({g.GoogleRating:F1})");
                }
                else
                {
                    lead.GoogleRating = g.GoogleRating;
                    lead.GoogleReviewCount = g.GoogleReviewCount;
                    lead.GoogleMapsUrl = g.GoogleMapsUrl;
                    // A rating a person typed that equals Google's stays the person's: only a value the server changed is recorded as written by it.
                    if (differs) HuntProvenance.Record(lead, new[] { "googleRating" });
                }
                result.GoogleNote = $"{g.GoogleRating:F1} from {g.GoogleReviewCount} reviews on the Google listing '{g.DisplayName}' (its website or phone number is this business's).";
            }
            else
            {
                result.GoogleNote = g == null ? "No Google listing found."
                    : $"The Google listing '{g.DisplayName}' does not show this business's website or one of its phone numbers, so it is not taken to be this business and no rating is used.";
                g = null;
            }
        }
        else result.GoogleNote = "Google Places is not configured on this server, so no Google rating could be checked.";

        if (g == null && existingLead && lead.GoogleRating != null && string.IsNullOrWhiteSpace(lead.GoogleMapsUrl))
        {
            var what = $"googleRating {lead.GoogleRating} (it is not from a Google listing)";
            if (clear)
            {
                removed?.Add(what);
                lead.GoogleRating = null;
                lead.GoogleReviewCount = null;
            }
            else kept?.Add(what);
        }
        var s = _scoring.CalculateScore(lead);
        lead.SalesPriorityScore = s.Score; lead.SalesPriorityLabel = s.Label; lead.SalesPriorityReasoning = s.Reasoning;
    }

    /// <summary>
    /// Which fields a verify may remove: only values the server itself wrote and nobody changed since (see <see cref="HuntProvenance"/>),
    /// or anything when the person confirmed. A value a person typed, or edited after a hunt wrote it, is never cleared without confirmation.
    /// </summary>
    public static Func<string, bool> MayClear(Lead lead, bool confirmed) => field => confirmed || HuntProvenance.Unchanged(lead, field);

    public static string KeptNote(List<string> kept) =>
        kept.Count == 0 ? "" : "\nNot removed, because they were entered by a person (confirm to remove them): " + string.Join("; ", kept);

    public record VerifyPreview(Guid LeadId, bool Reachable, int? ScoreBefore, int? ScoreAfter, bool WouldClear, List<object> Verified, List<object> NotCounted,
        List<string> WouldRemove, List<string> KeptBecauseEntered, List<string> Pages);

    /// <summary>What a verify would do to one lead, without saving anything (a dry run).</summary>
    public async Task<VerifyPreview> PreviewAsync(Lead lead, bool confirm, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CandidateBudget);
        var copy = (Lead)_db.Entry(lead).CurrentValues.ToObject();
        var before = _scoring.CalculateScore(copy).Score;
        var site = await _verifier.QuickLookAsync(lead.Website, budget.Token);
        if (!site.Reachable) return new VerifyPreview(lead.Id, false, before, before, false, new(), new(), new(), new(), new());
        await _verifier.DeepenAsync(site, budget.Token);
        var result = _verifier.VerifyDeterministic(site, copy.OwnerName);
        var mayClear = MayClear(copy, confirm);
        var kept = new List<string>();
        var removed = LeadVerifier.Apply(copy, result, clearUnverified: true, _scoring, kept, mayClear);
        await ApplyGoogleAsync(copy, result, budget.Token, existingLead: true, removed, mayClear("googleRating"), kept);
        return new VerifyPreview(lead.Id, true, before, copy.SalesPriorityScore, confirm || removed.Count > 0,
            result.Verified.Values.Select(c => (object)new { field = c.Field, value = c.Value, source = c.SourceUrl, quote = c.Quote }).ToList(),
            result.Dropped.Select(d => (object)new { field = d.Field, reason = d.Reason }).ToList(),
            removed, kept, result.PageUrls);
    }

    /// <summary>
    /// A Google listing is this business only when its website is on the business's own domain, or its phone number is one the business's
    /// pages publish. A name that merely shares a word proves nothing: another company, in another town, can have that name.
    /// </summary>
    public static bool ListingIsThisBusiness(GooglePlacesResult g, Lead lead, EvidenceFacts facts)
    {
        if (!string.IsNullOrWhiteSpace(g.Website) && UrlGuard.TryNormalize(g.Website, out var listed) && UrlGuard.TryNormalize(lead.Website, out var own)
            && UrlGuard.SiteKey(listed) == UrlGuard.SiteKey(own)) return true;
        var tail = HuntRegex.Replace(g.Phone ?? "", @"\D", "");
        if (tail.Length < 9) return false;
        tail = tail[^9..];
        string Tail(string n) { var d = HuntRegex.Replace(n, @"\D", ""); return d.Length >= 9 ? d[^9..] : ""; }
        return facts.Mobiles.Any(m => Tail(m.Normalized) == tail) || facts.Landlines.Any(l => Tail(l) == tail) || facts.TelNumbers.Any(t => Tail(t) == tail);
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
