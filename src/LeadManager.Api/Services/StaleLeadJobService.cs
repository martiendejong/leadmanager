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

        // Get all distinct user IDs that have leads
        var userIds = await db.Leads
            .Where(l => l.ImportedByUserId != null)
            .Select(l => l.ImportedByUserId!)
            .Distinct()
            .ToListAsync();

        int staleCreated = 0;
        int reminderCreated = 0;

        foreach (var userId in userIds)
        {
            // --- Stale lead warnings ---
            var staleLeads = await db.Leads
                .Where(l => l.ImportedByUserId == userId
                         && l.CreatedAt < staleThreshold)
                .ToListAsync();

            foreach (var lead in staleLeads)
            {
                // Avoid duplicate: skip if same stale notification created in last 24h
                var alreadyNotified = await db.Notifications.AnyAsync(n =>
                    n.UserId == userId
                    && n.LinkedLeadId == lead.Id
                    && n.Type == NotificationType.StaleLeadWarning
                    && n.CreatedAt >= dedupeWindow);

                if (!alreadyNotified)
                {
                    db.Notifications.Add(new Notification
                    {
                        UserId = userId,
                        Type = NotificationType.StaleLeadWarning,
                        Message = $"Lead '{lead.Name}' is al meer dan 7 dagen niet bijgewerkt.",
                        LinkedLeadId = lead.Id,
                        IsRead = false,
                        CreatedAt = now
                    });
                    staleCreated++;
                }
            }

            // --- Reminder due ---
            var dueReminders = await db.Leads
                .Where(l => l.ImportedByUserId == userId
                         && l.ReminderDate != null
                         && l.ReminderDate <= now)
                .ToListAsync();

            foreach (var lead in dueReminders)
            {
                var alreadyNotified = await db.Notifications.AnyAsync(n =>
                    n.UserId == userId
                    && n.LinkedLeadId == lead.Id
                    && n.Type == NotificationType.ReminderDue
                    && n.CreatedAt >= dedupeWindow);

                if (!alreadyNotified)
                {
                    db.Notifications.Add(new Notification
                    {
                        UserId = userId,
                        Type = NotificationType.ReminderDue,
                        Message = $"Herinnering voor lead '{lead.Name}' is verlopen.",
                        LinkedLeadId = lead.Id,
                        IsRead = false,
                        CreatedAt = now
                    });
                    reminderCreated++;
                }
            }
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Daily notification job complete: {StaleCount} stale warnings, {ReminderCount} reminders created for {UserCount} users",
            staleCreated, reminderCreated, userIds.Count);
    }
}
