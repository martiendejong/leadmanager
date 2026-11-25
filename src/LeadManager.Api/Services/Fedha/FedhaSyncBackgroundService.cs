using LeadManager.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Fedha;

/// <summary>Runs the Fedha invoice sync once a day (05:30 UTC) for the configured user. Does nothing while Fedha is not configured.</summary>
public class FedhaSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly FedhaOptions _opt;
    private readonly ILogger<FedhaSyncBackgroundService> _log;

    public FedhaSyncBackgroundService(IServiceScopeFactory scopes, FedhaOptions opt, ILogger<FedhaSyncBackgroundService> log)
    { _scopes = scopes; _opt = opt; _log = log; }

    public static TimeSpan DelayUntilNext(DateTime nowUtc)
    {
        var next = nowUtc.Date.AddHours(5).AddMinutes(30);
        if (next <= nowUtc) next = next.AddDays(1);
        return next - nowUtc;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Configured || string.IsNullOrWhiteSpace(_opt.SyncUserEmail))
        {
            _log.LogInformation("Fedha sync job idle: Fedha__ApiKey or Fedha__SyncUserEmail is not set.");
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(DelayUntilNext(DateTime.UtcNow), ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();
                var email = _opt.SyncUserEmail!.Trim().ToUpperInvariant();
                var userId = await db.Users.Where(u => u.NormalizedEmail == email).Select(u => u.Id).FirstOrDefaultAsync(ct);
                if (userId == null) { _log.LogWarning("Fedha sync: user {Email} not found.", _opt.SyncUserEmail); continue; }
                var r = await scope.ServiceProvider.GetRequiredService<FedhaSyncService>().RunAsync(userId, "daily", ct);
                _log.LogInformation("Fedha sync: {Created} created, {Updated} updated, {Unchanged} unchanged, {Unmatched} unmatched contacts.", r.Created, r.Updated, r.Unchanged, r.Unmatched.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "Fedha sync failed."); }
        }
    }
}
