using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services;

public class StaleLeadJobService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StaleLeadJobService> _logger;

    public StaleLeadJobService(IServiceScopeFactory scopeFactory, ILogger<StaleLeadJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunDailyNotificationsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();

        var now = DateTime.UtcNow;
        var staleThreshold = now.AddDays(-7);
        var dedupeWindow = now.AddHours(-24);

        // Pull all stale + due-reminder leads in one query, grouped by user.
        var staleLeads = await db.Leads
            .Where(l => l.ImportedByUserId != null && l.CreatedAt < staleThreshold)
            .Select(l => new { l.Id, l.Name, UserId = l.ImportedByUserId! })
            .ToListAsync();

        var dueReminders = await db.Leads
            .Where(l => l.ImportedByUserId != null && l.ReminderDate != null && l.ReminderDate <= now)
            .Select(l => new { l.Id, l.Name, UserId = l.ImportedByUserId! })
            .ToListAsync();

        // Batch-load all recent notifications (per user, per type) in a single query
        // — replaces the AnyAsync-in-foreach N+1 pattern from the previous implementation.
        var recentNotifications = await db.Notifications
            .Where(n => n.CreatedAt >= dedupeWindow
                     && (n.Type == NotificationType.StaleLeadWarning || n.Type == NotificationType.ReminderDue))
            .Select(n => new { n.UserId, n.Type, n.LinkedLeadId })
            .ToListAsync();

        var existingKeys = new HashSet<(string userId, NotificationType type, Guid? leadId)>(
            recentNotifications.Select(n => (n.UserId, n.Type, n.LinkedLeadId)));

        int staleCreated = 0;
        int reminderCreated = 0;

        foreach (var lead in staleLeads)
        {
            var key = (lead.UserId, NotificationType.StaleLeadWarning, (Guid?)lead.Id);
            if (existingKeys.Contains(key)) continue;

            db.Notifications.Add(new Notification
            {
                UserId = lead.UserId,
                Type = NotificationType.StaleLeadWarning,
                Message = $"Lead '{lead.Name}' is al meer dan 7 dagen niet bijgewerkt.",
                LinkedLeadId = lead.Id,
                IsRead = false,
                CreatedAt = now
            });
            existingKeys.Add(key);
            staleCreated++;
        }

        foreach (var lead in dueReminders)
        {
            var key = (lead.UserId, NotificationType.ReminderDue, (Guid?)lead.Id);
            if (existingKeys.Contains(key)) continue;

            db.Notifications.Add(new Notification
            {
                UserId = lead.UserId,
                Type = NotificationType.ReminderDue,
                Message = $"Herinnering voor lead '{lead.Name}' is verlopen.",
                LinkedLeadId = lead.Id,
                IsRead = false,
                CreatedAt = now
            });
            existingKeys.Add(key);
            reminderCreated++;
        }

        await db.SaveChangesAsync();

        var userCount = staleLeads.Select(l => l.UserId)
            .Concat(dueReminders.Select(l => l.UserId))
            .Distinct()
            .Count();

        _logger.LogInformation(
            "Daily notification job complete: {StaleCount} stale warnings, {ReminderCount} reminders created for {UserCount} users",
            staleCreated, reminderCreated, userCount);
    }
}
