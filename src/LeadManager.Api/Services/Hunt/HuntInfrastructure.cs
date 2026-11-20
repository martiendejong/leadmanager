using System.Collections.Concurrent;
using System.Threading.Channels;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Hunt;

/// <summary>Verify jobs waiting to run. One at a time, so the server never crawls for many users at once.</summary>
public class HuntQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public ChannelWriter<Guid> Writer => _channel.Writer;
    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>Lets a cancel request stop the running job at once, not only between candidates.</summary>
public class HuntRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    public void Register(Guid id, CancellationTokenSource cts) => _running[id] = cts;
    public void Unregister(Guid id) => _running.TryRemove(id, out _);
    public bool Cancel(Guid id)
    {
        if (!_running.TryGetValue(id, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }
}

public class HuntBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly HuntQueue _queue;
    private readonly HuntRegistry _registry;
    private readonly ILogger<HuntBackgroundService> _logger;

    public HuntBackgroundService(IServiceScopeFactory scopes, HuntQueue queue, HuntRegistry registry, ILogger<HuntBackgroundService> logger)
    {
        _scopes = scopes; _queue = queue; _registry = registry; _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // A restart interrupts a running verify job; mark it so the screen does not show it as running forever, and pick up queued ones.
        // Hunt jobs are not touched: the Jengo agent keeps running on its own and the sweep ends the ones that go silent.
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();
            var stale = await db.HuntJobs.Where(j => j.Kind == HuntKinds.Verify && j.Status == HuntStatuses.Running).ToListAsync(cancellationToken);
            foreach (var j in stale)
            {
                j.Status = HuntStatuses.Failed;
                j.Error = "The server restarted while this job was running. The candidates that were finished are saved.";
                j.CompletedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);
            foreach (var id in await db.HuntJobs.Where(j => j.Kind == HuntKinds.Verify && j.Status == HuntStatuses.Pending).OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync(cancellationToken))
                _queue.Writer.TryWrite(id);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not recover hunt jobs at startup"); }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _registry.Register(id, cts);
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<HuntService>().RunAsync(id, cts.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Unhandled error in hunt job {JobId}", id); }
                finally { _registry.Unregister(id); }
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Ends hunts whose Jengo agent went silent (never started, crashed or was stopped) so they do not look alive forever.</summary>
public class HuntSweepService : BackgroundService
{
    /// <summary>A running hunt whose agent has not called for this long has stopped.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromMinutes(45);
    /// <summary>A hunt that nobody started (no call at all) is ended after this long: as long as the task token lives, and a person has to start the task.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(60);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HuntSweepService> _logger;

    public HuntSweepService(IServiceScopeFactory scopes, ILogger<HuntSweepService> logger) { _scopes = scopes; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();
                    var n = await HuntService.SweepStaleAsync(db, DateTime.UtcNow, Silence);
                    if (n > 0) _logger.LogInformation("Marked {Count} silent hunt job(s) as failed", n);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Hunt sweep failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Per-user and per-job locks, so the one-job-per-user rule and the counters of a hunt can never be raced.</summary>
public static class HuntLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Users = new();
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Jobs = new();

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => Interlocked.Exchange(ref _sem, null)?.Release();
    }

    /// <summary>Waits up to <paramref name="wait"/> for the lock of this user; null when it could not be had.</summary>
    public static async Task<IDisposable?> EnterUserAsync(string userId, TimeSpan wait)
    {
        var sem = Users.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        return await sem.WaitAsync(wait) ? new Releaser(sem) : null;
    }

    /// <summary>The lock of a job that has ended is not needed any more (an in-flight holder keeps its own reference).</summary>
    public static void Forget(Guid jobId) => Jobs.TryRemove(jobId, out _);

    /// <summary>One candidate of a hunt at a time: null when another submission of this hunt is being checked.</summary>
    public static IDisposable? TryEnterJob(Guid jobId)
    {
        var sem = Jobs.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));
        return sem.Wait(0) ? new Releaser(sem) : null;
    }
}

/// <summary>
/// The token of one hunt: 32 random bytes, handed to the agent once (in the task text) and stored only as a SHA-256 hash on the job. It opens
/// the agent endpoints of that hunt only, for <see cref="Lifetime"/>, and is revoked (hash removed) when the hunt completes, fails, is
/// cancelled or goes silent. It is not a user login and not an API key; nothing else accepts it.
/// </summary>
public static class HuntTokens
{
    /// <summary>The task token (in the task text) is good for this long, and only for the first /brief call that exchanges it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(60);
    /// <summary>The session token stays valid this long after the last call of the agent (sliding).</summary>
    public static readonly TimeSpan SessionIdle = TimeSpan.FromMinutes(30);
    /// <summary>Whatever the agent does, no token of a hunt works longer than this after the hunt was created.</summary>
    public static readonly TimeSpan HardLimit = TimeSpan.FromHours(3);

    private static string Hash(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string NewToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashOf(string token) => Hash(token);

    /// <summary>Creates the TASK token for the job, stores its hash and expiry on the job and returns the token (the only time it exists in clear).</summary>
    public static string Issue(HuntJob job, DateTime? now = null)
    {
        var token = NewToken();
        job.TokenHash = Hash(token);
        job.TokenExchanged = false;
        job.TokenExpiresAt = (now ?? DateTime.UtcNow) + Lifetime;
        return token;
    }

    /// <summary>When a session token issued now expires: <see cref="SessionIdle"/> from now, never later than <see cref="HardLimit"/> after the hunt was created.</summary>
    public static DateTime SessionExpiry(HuntJob job, DateTime now)
    {
        var hard = job.CreatedAt + HardLimit;
        var idle = now + SessionIdle;
        return idle < hard ? idle : hard;
    }

    /// <summary>A fresh session token (hash and expiry for the caller to store) in place of the task token.</summary>
    public static (string Token, string Hash, DateTime ExpiresAt) NewSession(HuntJob job, DateTime? now = null)
    {
        var token = NewToken();
        return (token, Hash(token), SessionExpiry(job, now ?? DateTime.UtcNow));
    }

    public static void Revoke(HuntJob job)
    {
        job.TokenHash = null;
        job.TokenExpiresAt = null;
        job.TokenExchanged = false;
    }

    /// <summary>True when the given token is the live token of this job: right hash, not expired, not revoked.</summary>
    public static bool Valid(HuntJob job, string? given, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(given) || given.Length > 200 || job.TokenHash == null || job.TokenExpiresAt == null) return false;
        if ((now ?? DateTime.UtcNow) >= job.TokenExpiresAt.Value) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(Hash(given)), System.Text.Encoding.ASCII.GetBytes(job.TokenHash));
    }
}

/// <summary>
/// Failed agent logins are charged much harder than the rate limiter does (which counts every call alike): after a few wrong tokens from one
/// address, or for one hunt, the agent endpoints answer 429 before they look at the token at all. Counters are in memory and per window.
/// </summary>
public static class HuntAuthThrottle
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int MaxFailuresPerAddress = 20;
    public const int MaxFailuresPerHunt = 10;

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> Failures = new();

    private static int Count(string key, DateTime now)
    {
        if (!Failures.TryGetValue(key, out var q)) return 0;
        while (q.TryPeek(out var t) && now - t > Window) q.TryDequeue(out _);
        return q.Count;
    }

    /// <summary>True when this address, or this address for this hunt, has had too many wrong tokens lately.</summary>
    public static bool Blocked(string? address, Guid huntId, DateTime? now = null)
    {
        var n = now ?? DateTime.UtcNow;
        // Per hunt AND address: somebody who only knows the hunt id (it stands in the task text) cannot lock the real agent out from another address.
        return (address != null && Count("ip|" + address, n) >= MaxFailuresPerAddress) || Count("id|" + huntId + "|" + (address ?? "unknown"), n) >= MaxFailuresPerHunt;
    }

    public static void Fail(string? address, Guid huntId, DateTime? now = null)
    {
        var n = now ?? DateTime.UtcNow;
        if (address != null) Failures.GetOrAdd("ip|" + address, _ => new()).Enqueue(n);   // an unreadable address is not charged to everybody at once
        Failures.GetOrAdd("id|" + huntId + "|" + (address ?? "unknown"), _ => new()).Enqueue(n);
        if (Failures.Count > 20_000) foreach (var k in Failures.Keys.ToList()) if (Count(k, n) == 0) Failures.TryRemove(k, out _);
    }

    /// <summary>For tests.</summary>
    public static void Reset() => Failures.Clear();
}

/// <summary>What a deleted lead leaves behind in the hunt history: the row stays (name, website, score, reason), its evidence goes.</summary>
public static class HuntEvidence
{
    /// <summary>The quotes and facts of the given leads' candidate rows are emptied; the database link is set to null by the foreign key.</summary>
    public static async Task ForgetAsync(LeadManagerDbContext db, IReadOnlyCollection<Guid> leadIds, Guid? moveTo = null)
    {
        if (leadIds.Count == 0) return;
        if (moveTo != null)   // a merge: the history follows the lead that stays
            await db.HuntCandidates.Where(c => c.LeadId != null && leadIds.Contains(c.LeadId.Value)).ExecuteUpdateAsync(s => s.SetProperty(c => c.LeadId, moveTo));
        else
            await db.HuntCandidates.Where(c => c.LeadId != null && leadIds.Contains(c.LeadId.Value)).ExecuteUpdateAsync(s => s.SetProperty(c => c.EvidenceJson, "{}").SetProperty(c => c.LeadId, (Guid?)null));
    }
}
