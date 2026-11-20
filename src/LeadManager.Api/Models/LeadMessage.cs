namespace LeadManager.Api.Models;

public static class MessageChannels
{
    public const string Email = "Email";
    public const string LinkedIn = "LinkedIn";
    public const string WhatsApp = "WhatsApp";
    public const string Phone = "Phone";
    public static readonly string[] All = { Email, LinkedIn, WhatsApp, Phone };

    /// <summary>The channel in its canonical spelling ("email" gives "Email"), or null when it is not one of ours.</summary>
    public static string? Canonical(string? channel) =>
        All.FirstOrDefault(c => string.Equals(c, (channel ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class MessageStatuses
{
    public const string Draft = "Draft";
    public const string Sent = "Sent";
}

/// <summary>
/// A message that somebody composed for a lead. The app NEVER sends anything: a draft is text, "Sent" only records that the user sent it
/// themselves (by mail app, LinkedIn, WhatsApp or phone) and said so with a confirmation.
/// </summary>
public class LeadMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }
    public string UserId { get; set; } = "";
    public string Channel { get; set; } = MessageChannels.Email;
    /// <summary>One line, for e-mail only (empty for the other channels).</summary>
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = MessageStatuses.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
}

/// <summary>
/// A message template of one user: per channel, and per product key of the catalog (empty = the default for the channel). Written in the
/// language of the people who will read the message (Dutch); the placeholders are {name}, {owner}, {why}, {need}, {product} and {reason}.
/// </summary>
public class MessageTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Channel { get; set; } = MessageChannels.Email;
    /// <summary>A key of the product catalog, or "" for the default of the channel.</summary>
    public string ProductKey { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
