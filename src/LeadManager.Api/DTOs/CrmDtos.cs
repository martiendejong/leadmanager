namespace LeadManager.Api.DTOs;

// Jengo Clients: CRM DTOs (task 8023)

public record ContactDto(
    Guid Id, Guid? LeadId, Guid? ClientId, string Name, string? Role, string? Email,
    string? Phone, string? Mobile, string? LinkedInUrl, string? PreferredChannel,
    string? Notes, DateTime CreatedAt);

public record UpsertContactDto(
    string Name, string? Role, string? Email, string? Phone, string? Mobile,
    string? LinkedInUrl, string? PreferredChannel, string? Notes);

public record InteractionDto(
    Guid Id, Guid? LeadId, Guid? ClientId, Guid? ContactId, string Kind, string Direction,
    DateTime OccurredAt, string Summary, string? Outcome, string? SourceRef,
    string? CreatedByUserId, DateTime CreatedAt);

public record CreateInteractionDto(
    string Kind, string? Direction, DateTime? OccurredAt, string Summary,
    string? Outcome, Guid? ContactId, string? SourceRef);

public record NextActionDto(
    Guid Id, Guid? LeadId, Guid? ClientId, Guid? ContactId, string Title, DateTime DueAt,
    string? AssignedToUserId, DateTime? DoneAt, DateTime CreatedAt);

public record CreateNextActionDto(string Title, DateTime DueAt, Guid? ContactId, string? AssignedToUserId);

public record UpdateNextActionDto(string? Title, DateTime? DueAt, string? AssignedToUserId);

/// <summary>A next action with enough parent context to start the call without opening the lead.</summary>
public record TodayActionDto(
    Guid Id, string ParentType, Guid ParentId, string ParentName, string? Phone,
    string Title, DateTime DueAt, bool Overdue);

public record EntityLinkDto(
    Guid Id, Guid? LeadId, Guid? ClientId, string Kind, string Label, string? Url,
    string? ExternalId, DateTime CreatedAt);

public record CreateEntityLinkDto(string Kind, string Label, string? Url, string? ExternalId);

/// <summary>One row of the unified history of a lead or client.</summary>
public record TimelineItemDto(
    string Source, Guid Id, string Kind, string? Direction, DateTime At,
    string Summary, string? Outcome, string? SourceRef);

public record CreateApiKeyDto(string Name);

public record ApiKeyDto(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

/// <summary>Returned once, at creation. The plain key is never stored.</summary>
public record CreatedApiKeyDto(Guid Id, string Name, string Prefix, string Key);

public record CrmMatchDto(
    string ParentType, Guid ParentId, string Name, string? Website, string? Email,
    string? Phone, string MatchedOn);
