using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LeadManager.Api.Hubs;

[Authorize]
public class EnrichmentHub : Hub
{
    public async Task JoinJob(string jobId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, $"job-{jobId}");
}
