using LeadManager.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Hubs;

[Authorize]
public class EnrichmentHub : Hub
{
    private readonly LeadManagerDbContext _db;

    public EnrichmentHub(LeadManagerDbContext db) => _db = db;

    /// <summary>
    /// Joins the live feed of ONE job, only when the signed-in user owns it (a hunt, a verify run or an enrichment run). The group name is made from the
    /// id that is stored, not from the text that was sent, and a job that is not yours answers exactly like one that does not exist.
    /// </summary>
    public async Task JoinJob(string jobId)
    {
        var userId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(jobId, out var id)) throw new HubException("Job not found.");
        var owns = await _db.HuntJobs.AsNoTracking().AnyAsync(j => j.Id == id && j.UserId == userId)
                   || await _db.EnrichmentJobs.AsNoTracking().AnyAsync(j => j.Id == id && j.RequestedByUserId == userId);
        if (!owns) throw new HubException("Job not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"job-{id}");
    }
}
