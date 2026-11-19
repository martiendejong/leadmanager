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
    public static readonly TimeSpan Silence = TimeSpan.FromMinutes(45);
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
/// The token of one hunt: HMAC-SHA256 of the hunt id with the server secret. It opens the agent endpoints of that hunt only, and only while
/// that hunt is Pending or Running, so it is useless after the hunt ends and for every other hunt. The task text may contain it.
/// </summary>
public static class HuntTokens
{
    public static string For(string secret, Guid huntId)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var mac = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("hunt-agent:" + huntId.ToString("D")));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool Valid(string secret, Guid huntId, string? given)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(given)) return false;
        var a = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(given));
        var b = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(For(secret, huntId)));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
