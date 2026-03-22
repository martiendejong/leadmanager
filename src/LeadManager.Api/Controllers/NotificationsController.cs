using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadManager.Api.Controllers;

[Route("api/notifications")]
[ApiController]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly NotificationService _notifications;

    public NotificationsController(NotificationService notifications)
    {
        _notifications = notifications;
    }

    private string? GetCurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/notifications — list unread for current user
    [HttpGet]
    public async Task<IActionResult> GetNotifications()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var notifications = await _notifications.GetUnreadAsync(userId);
        return Ok(notifications.Select(n => new
        {
            id = n.Id,
            type = n.Type.ToString(),
            message = n.Message,
            linkedLeadId = n.LinkedLeadId,
            isRead = n.IsRead,
            createdAt = n.CreatedAt
        }));
    }

    // GET /api/notifications/count — unread count for badge
    [HttpGet("count")]
    public async Task<IActionResult> GetCount()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var count = await _notifications.GetUnreadCountAsync(userId);
        return Ok(new { count });
    }

    // PUT /api/notifications/{id}/read — mark one as read
    [HttpPut("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var success = await _notifications.MarkReadAsync(id, userId);
        if (!success) return NotFound();

        return Ok();
    }

    // PUT /api/notifications/read-all — mark all as read
    [HttpPut("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var count = await _notifications.MarkAllReadAsync(userId);
        return Ok(new { marked = count });
    }
}
