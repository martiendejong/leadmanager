using LeadManager.Api.Services;
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

    /// <summary>The domains of the leads of this user (within one organisation when given), so the agent does not research a company that is already known.</summary>
    public async Task<List<string>> KnownDomainsAsync(string? userId, int max = 1500, Guid? organizationId = null)
    {
        var sites = await _db.Leads.AsNoTracking().VisibleTo(_db, userId)
            .Where(l => organizationId == null || l.OrganizationId == organizationId).Select(l => l.Website).ToListAsync();
        return sites.Select(UrlGuard.DomainOf).Where(d => d != "").Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>Of the given domains, the ones that are the website of a lead of this user.</summary>
    public async Task<List<string>> KnownAmongAsync(string? userId, IReadOnlyCollection<string> domains, Guid? organizationId = null)
    {
        if (domains.Count == 0) return new();
        var known = (await KnownDomainsAsync(userId, int.MaxValue, organizationId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return domains.Where(known.Contains).ToList();
    }

    /// <summary>One candidate (all its pages, the verification and the save) may take this long.</summary>
    public static readonly TimeSpan CandidateBudget = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Verifies one submitted candidate. The caller holds the lock of the job (<see cref="HuntLocks.TryEnterJob"/>), so counters are never
    /// updated by two submissions at once; the stop conditions are checked again after the slow part, before anything is saved.
    /// </summary>
    public async Task<SubmissionResult> SubmitCandidateAsync(HuntJob job, Submission sub, CancellationToken ct)
    {
        await TouchAsync(job);
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
            var knownDomains = await KnownDomainsAsync(job.UserId, int.MaxValue, job.OrganizationId);
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
                        ImportedByUserId = job.UserId, OrganizationId = job.OrganizationId, ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
                    };
                    LeadVerifier.Apply(lead, result, clearUnverified: false, _scoring);
                    await ApplyGoogleAsync(lead, result, budget.Token);
                    LeadInsight.Apply(lead, result);   // why, needs and product fit: the verified parts only
                    var score = lead.SalesPriorityScore ?? 0;
                    cand.Score = score;
                    cand.VerifiedScore = score;   // a new lead holds only what the server verified
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

        var saved = lead != null && note != null && await SaveLeadAsync(job, cand, lead, note);
        // A company the user already has is not research: it does not use up one of the candidates of the hunt.
        if (cand.Outcome != HuntOutcomes.Duplicate) job.Screened++;
        if (saved)
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
        await TouchAsync(job);
        SubmissionResult Done(string outcome, string reason, int? score = null, Guid? id = null, bool dup = false) =>
            new(outcome, score, reason, id, dup, job.BestScore, job.Qualified, job.Screened, ShouldStop(job));

        if (ShouldStop(job)) return Done(HuntOutcomes.Rejected, job.CancelRequested ? "The review was cancelled." : "The review already covered all its leads.");
        if (!job.LeadIds.Contains(leadId)) return Done(HuntOutcomes.Rejected, "This lead is not part of this review.");
        if (await _db.HuntCandidates.AnyAsync(c => c.JobId == job.Id && c.LeadId == leadId, CancellationToken.None))
            return Done(HuntOutcomes.Duplicate, "This lead was already reviewed in this run.", dup: true);
        var lead = await _db.Leads.VisibleTo(_db, job.UserId).FirstOrDefaultAsync(l => l.Id == leadId, CancellationToken.None);
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

                // The hunt may have been cancelled or finished while the pages were fetched: nothing is applied then.
                await _db.Entry(job).ReloadAsync(CancellationToken.None);
                if (ShouldStop(job)) return Done(HuntOutcomes.Rejected, job.CancelRequested ? "The review was cancelled while this lead was being checked. It was left as it is." : "The review ended while this lead was being checked. It was left as it is.");

                var result = _verifier.VerifyClaims(site, claims, lead.OwnerName);
                var kept = new List<string>();
                var removed = LeadVerifier.Apply(lead, result, clearUnverified: false, _scoring, kept);
                await ApplyGoogleAsync(lead, result, budget.Token, existingLead: true, removed, clear: false, kept);
                LeadInsight.Apply(lead, result);
                var score = lead.SalesPriorityScore ?? 0;
                var (verifiedScore, unverified) = VerifiedOnlyScore(lead, result);
                var claimedNote = claimedScore is int c && c != score ? $" The agent claimed {c}; the server verified {score}." : "";
                cand.Score = score;
                cand.VerifiedScore = verifiedScore;
                cand.MaxReachable = LeadVerifier.MaxReachable(result.Facts, _places.IsConfigured);
                cand.Outcome = HuntOutcomes.Verified;
                cand.Reason = verifiedScore == score
                    ? $"Reviewed by the agent and verified by the server: score {score}." + claimedNote
                    : $"Reviewed by the agent: stored score {score}, verified-only score {verifiedScore} (the difference rests on values nobody could verify: {string.Join("; ", unverified)})." + claimedNote;
                cand.EvidenceJson = LeadVerifier.BuildEvidenceJson(result, cand.MaxReachable);
                var title = "Agent review " + DateTime.UtcNow.ToString("yyyy-MM-dd");
                _db.LeadNotes.Add(new LeadNote { LeadId = lead.Id, CreatedByUserId = job.UserId ?? "",
                    Content = LeadVerifier.BuildNote(title, score, result, removed, verifiedScore, unverified) + KeptNote(kept) + claimedNote });

                // Once more right before the save: a cancel that arrived during the Google check also stops this lead from being changed.
                await _db.Entry(job).ReloadAsync(CancellationToken.None);
                if (ShouldStop(job))
                {
                    foreach (var added in _db.ChangeTracker.Entries<LeadNote>().Where(e => e.State == EntityState.Added).ToList()) added.State = EntityState.Detached;
                    await _db.Entry(lead).ReloadAsync(CancellationToken.None);
                    return Done(HuntOutcomes.Rejected, job.CancelRequested ? "The review was cancelled while this lead was being checked. It was left as it is." : "The review ended while this lead was being checked. It was left as it is.");
                }
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

    /// <summary>
    /// The agent called: the job is running and alive. A CONDITIONAL update (only while the job is Pending or Running), so a hunt that was cancelled or
    /// ended a moment ago is never brought back to life by a late call; the job is reloaded afterwards, so it shows what the database says.
    /// A session token lives on while the agent works (sliding), never past the hard limit.
    /// </summary>
    public async Task<bool> TouchAsync(HuntJob job)
    {
        var id = job.Id;
        var now = DateTime.UtcNow;
        var slide = job.TokenExchanged && job.TokenHash != null;
        var expiry = HuntTokens.SessionExpiry(job, now);
        var rows = await _db.HuntJobs.Where(j => j.Id == id && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LastAgentCallAt, now)
                .SetProperty(j => j.Status, j => j.Status == HuntStatuses.Pending ? HuntStatuses.Running : j.Status)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now)
                .SetProperty(j => j.TokenExpiresAt, j => slide ? (DateTime?)expiry : j.TokenExpiresAt), CancellationToken.None);
        await _db.Entry(job).ReloadAsync(CancellationToken.None);
        return rows == 1;
    }

    /// <summary>Ends the hunt, once: only a job that is still Pending or Running is changed (an ended or cancelled hunt stays as it is).</summary>
    public async Task CompleteAsync(HuntJob job, string? summary, bool failed)
    {
        await TouchAsync(job);
        var id = job.Id;
        var clipped = Clip(summary, 1500);
        var error = failed ? Clip(summary, 500) ?? "The agent reported a failure." : null;
        var status = failed ? HuntStatuses.Failed : job.CancelRequested ? HuntStatuses.Cancelled : HuntStatuses.Completed;
        var now = DateTime.UtcNow;
        await _db.HuntJobs.Where(j => j.Id == id && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.Summary, clipped)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.CurrentCandidate, (string?)null)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.TokenHash, (string?)null)
                .SetProperty(j => j.TokenExpiresAt, (DateTime?)null)
                .SetProperty(j => j.TokenExchanged, false), CancellationToken.None);
        await _db.Entry(job).ReloadAsync(CancellationToken.None);
        HuntLocks.Forget(job.Id);
        await Push(job, null);
    }

    /// <summary>The agent says what it is working on. Only the display text is taken from it; the counters are the server's.</summary>
    public async Task ProgressAsync(HuntJob job, string? current)
    {
        if (!await TouchAsync(job)) return;   // the hunt ended: the display text is not touched
        var text = TextSafety.Clean(current, MaxNameLength, dropOrders: false);
        job.CurrentCandidate = text.Length == 0 ? null : text;
        await _db.SaveChangesAsync(CancellationToken.None);
        await Push(job, null);
    }

    /// <summary>
    /// Hunts whose agent has been silent for too long are marked failed, so the screen never shows them as running forever. A hunt that nobody
    /// started (the agent never called) gets <paramref name="pendingGrace"/> (the task has to be started by a person on this board), a hunt that
    /// was running gets <paramref name="silence"/> since the last call. Each job is changed with a conditional update, so a hunt that finished a moment ago stays finished.
    /// </summary>
    public static async Task<int> SweepStaleAsync(LeadManagerDbContext db, DateTime now, TimeSpan silence, TimeSpan? pendingGrace = null)
    {
        var grace = pendingGrace ?? HuntSweepService.PendingGrace;
        var silentLimit = now - silence;
        var pendingLimit = now - grace;
        var stale = await db.HuntJobs.AsNoTracking().Where(j => (j.Kind == HuntKinds.Hunt || j.Kind == HuntKinds.Review) && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running)
            && (j.LastAgentCallAt != null ? j.LastAgentCallAt < silentLimit : j.CreatedAt < pendingLimit))
            .Select(j => new { j.Id, Started = j.LastAgentCallAt != null }).ToListAsync();
        var ended = 0;
        foreach (var j in stale)
        {
            var id = j.Id;
            var message = j.Started
                ? $"The Jengo agent stopped reporting: no call in {(int)silence.TotalMinutes} minutes. The candidates that were finished are saved."
                : $"Nobody started the Jengo agent task within {(int)grace.TotalMinutes} minutes, so the hunt was ended. Start the task on JengoWork (move it to todo) right after you start a hunt, or start the hunt again.";
            var rows = await db.HuntJobs.Where(x => x.Id == id && (x.Status == HuntStatuses.Pending || x.Status == HuntStatuses.Running))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, HuntStatuses.Failed).SetProperty(x => x.Error, message).SetProperty(x => x.CompletedAt, now)
                    .SetProperty(x => x.TokenHash, (string?)null).SetProperty(x => x.TokenExpiresAt, (DateTime?)null).SetProperty(x => x.TokenExchanged, false));
            if (rows > 0) { ended++; HuntLocks.Forget(id); }
        }
        if (ended > 0) db.ChangeTracker.Clear();
        return ended;
    }

    /// <summary>
    /// The score the lead would have with ONLY what the server verified: the same lead with every score bearing value that no verified claim
    /// backs removed (and a rating that is not from a Google listing). The second value lists those values: they are in the stored score but unproven.
    /// </summary>
    public (int Score, List<string> Unverified) VerifiedOnlyScore(Lead lead, VerificationResult result)
    {
        var copy = (Lead)_db.Entry(lead).CurrentValues.ToObject();
        var unverified = LeadVerifier.Apply(copy, result, clearUnverified: true, _scoring);
        LeadVerifier.ClearUnsourcedGoogle(copy, unverified, _scoring);
        return (copy.SalesPriorityScore ?? 0, unverified);
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
            var duplicate = await _db.Leads.AnyAsync(l => l.OrganizationId == job.OrganizationId && l.Name == lead.Name && l.Website == lead.Website, CancellationToken.None);
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

            var lead = await _db.Leads.VisibleTo(_db, job.UserId).FirstOrDefaultAsync(l => l.Id == id, ct);
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
