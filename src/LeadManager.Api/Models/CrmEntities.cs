namespace LeadManager.Api.Models;

// Jengo Clients: CRM core entities (task 8023).
// Each entity hangs off exactly ONE parent: a Lead OR a Client (enforced by a DB check
// constraint). Links are references, not copies: mail, tasks and knowledge stay in
// their own tools (see proposals/2026-11-lead-crm/PROPOSAL.md).

public enum InteractionKind { Call, Email, WhatsApp, Meeting, Note }

public enum InteractionDirection { Outbound, Inbound, Internal }

public enum EntityLinkKind
{
    JengoWorkBoard,
    JengoWorkTask,
    KnowledgeProject,
    MailThread,
    MeetSession,
    WhatsAppChat,
    GitHubRepo,
    Document,
    Web
}

public class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LeadId { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = "";
    public string? Role { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? PreferredChannel { get; set; }
    public string? Notes { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Interaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LeadId { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? ContactId { get; set; }
    public InteractionKind Kind { get; set; } = InteractionKind.Note;
    public InteractionDirection Direction { get; set; } = InteractionDirection.Outbound;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = "";
    /// <summary>Free-form outcome label, e.g. "geen gehoor", "terugbellen", "interesse".</summary>
    public string? Outcome { get; set; }
    /// <summary>Id in the source system (mail uid, meeting id, WhatsApp message id) for idempotent imports.</summary>
    public string? SourceRef { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class NextAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LeadId { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? ContactId { get; set; }
    public string Title { get; set; } = "";
    public DateTime DueAt { get; set; } = DateTime.UtcNow;
    public string? AssignedToUserId { get; set; }
    public DateTime? DoneAt { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class EntityLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LeadId { get; set; }
    public Guid? ClientId { get; set; }
    public EntityLinkKind Kind { get; set; } = EntityLinkKind.Web;
    public string Label { get; set; } = "";
    public string? Url { get; set; }
    /// <summary>Id in the target system (board id, task id, thread id, project id).</summary>
    public string? ExternalId { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>API key for agents and other Jengo tools. Only the SHA-256 hash is stored.</summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
