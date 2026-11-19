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
