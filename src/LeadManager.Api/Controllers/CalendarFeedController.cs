using System.Security.Cryptography;
using System.Text;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>
/// Personal calendar feed (ICS) with my open next actions. Google Calendar, Apple and Outlook can subscribe to
/// the URL. One way: Jengo Clients publishes, the calendar reads. The token in the URL is the only credential,
/// so it is long, random and can be replaced at any time.
/// </summary>
[ApiController]
public class CalendarFeedController : ControllerBase
{
    private readonly LeadManagerDbContext _db;

    public CalendarFeedController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private string FeedUrl(string token) => $"{Request.Scheme}://{Request.Host}/api/calendar/feed/{token}.ics";

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    // GET /api/calendar/feed  (creates the feed on first use)
    [HttpGet("api/calendar/feed")]
    [Authorize]
    public async Task<IActionResult> Get()
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var feed = await _db.CalendarFeeds.FirstOrDefaultAsync(f => f.UserId == userId);
        if (feed == null)
        {
            feed = new CalendarFeed { UserId = userId, Token = NewToken() };
            _db.CalendarFeeds.Add(feed);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Two first requests raced; the unique index let one through. Use that one.
                _db.ChangeTracker.Clear();
                feed = await _db.CalendarFeeds.FirstAsync(f => f.UserId == userId);
            }
        }
        return Ok(new CalendarFeedDto(feed.Token, FeedUrl(feed.Token)));
    }

    // POST /api/calendar/feed/rotate  (the old URL stops working at once)
    [HttpPost("api/calendar/feed/rotate")]
    [Authorize]
    public async Task<IActionResult> Rotate()
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var feed = await _db.CalendarFeeds.FirstOrDefaultAsync(f => f.UserId == userId);
        if (feed == null)
        {
            feed = new CalendarFeed { UserId = userId };
            _db.CalendarFeeds.Add(feed);
        }
        feed.Token = NewToken();
        feed.CreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new CalendarFeedDto(feed.Token, FeedUrl(feed.Token)));
    }

    // GET /api/calendar/feed/{token}.ics  (anonymous: the token is the credential)
    [HttpGet("api/calendar/feed/{token}.ics")]
    [AllowAnonymous]
    public async Task<IActionResult> Ics(string token)
    {
        if (token.Length != 48) return NotFound();
        var feed = await _db.CalendarFeeds.FirstOrDefaultAsync(f => f.Token == token);
        if (feed == null) return NotFound();

        var now = DateTime.UtcNow;
        var from = now.AddDays(-60);
        var to = now.AddDays(540);
        var userId = feed.UserId;

        var actions = await _db.NextActions
            .Where(a => a.AssignedToUserId == userId && a.DoneAt == null && a.DueAt >= from && a.DueAt <= to)
            .OrderBy(a => a.DueAt).Take(2000).ToListAsync();
        var leadIds = actions.Where(a => a.LeadId.HasValue).Select(a => a.LeadId!.Value).Distinct().ToList();
        var clientIds = actions.Where(a => a.ClientId.HasValue).Select(a => a.ClientId!.Value).Distinct().ToList();
        var leads = await _db.Leads.Where(l => leadIds.Contains(l.Id) && l.ImportedByUserId == userId)
            .ToDictionaryAsync(l => l.Id, l => l.Name);
        var clients = await _db.Clients.Where(c => clientIds.Contains(c.Id) && c.CreatedByUserId == userId)
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var sb = new StringBuilder();
        void Line(string l)
        {
            // RFC 5545: lines are folded at 75 octets; continuation lines start with a space.
            var bytes = Encoding.UTF8.GetBytes(l);
            if (bytes.Length <= 75) { sb.Append(l).Append("\r\n"); return; }
            var cur = new StringBuilder();
            var curBytes = 0;
            var first = true;
            foreach (var rune in l.EnumerateRunes())
            {
                var n = rune.Utf8SequenceLength;
                var limit = first ? 75 : 74;
                if (curBytes + n > limit)
                {
                    sb.Append(first ? "" : " ").Append(cur).Append("\r\n");
                    cur.Clear(); curBytes = 0; first = false;
                }
                cur.Append(rune.ToString());
                curBytes += n;
            }
            if (cur.Length > 0) sb.Append(first ? "" : " ").Append(cur).Append("\r\n");
        }

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Jengo Clients//Acties//NL");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("X-WR-CALNAME:Jengo Clients");
        Line("REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Line("X-PUBLISHED-TTL:PT1H");

        foreach (var a in actions)
        {
            string? name = null;
            string path;
            if (a.LeadId.HasValue) { leads.TryGetValue(a.LeadId.Value, out name); path = $"/leads/{a.LeadId}"; }
            else if (a.ClientId.HasValue) { clients.TryGetValue(a.ClientId.Value, out name); path = $"/clients/{a.ClientId}"; }
            else continue;
            if (name == null) continue;

            var start = a.DueAt.Kind == DateTimeKind.Utc ? a.DueAt : DateTime.SpecifyKind(a.DueAt, DateTimeKind.Utc);
            Line("BEGIN:VEVENT");
            Line($"UID:{a.Id}@jengo-clients");
            Line($"DTSTAMP:{a.CreatedAt:yyyyMMdd'T'HHmmss'Z'}");
            Line($"DTSTART:{start:yyyyMMdd'T'HHmmss'Z'}");
            Line($"DTEND:{start.AddMinutes(30):yyyyMMdd'T'HHmmss'Z'}");
            Line($"SUMMARY:{Escape(a.Title)} · {Escape(name)}");
            Line($"DESCRIPTION:{Escape($"{name}: {a.Title}\n{baseUrl}{path}")}");
            Line($"URL:{baseUrl}{path}");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");

        Response.Headers.CacheControl = "private, max-age=300";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(sb.ToString(), "text/calendar; charset=utf-8");
    }

    private static string Escape(string v) => v
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
        .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "");
}
