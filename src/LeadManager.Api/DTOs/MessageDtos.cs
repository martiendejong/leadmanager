namespace LeadManager.Api.DTOs;

public record LeadMessageDto(
    Guid Id, Guid LeadId, string LeadName, string Channel, string Subject, string Body, string Status,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? SentAt, string? LeadPipelineStatus, int? LeadScore);

public record CreateMessageDto(string? Channel, string? Subject, string? Body);
public record UpdateMessageDto(string? Channel, string? Subject, string? Body);

/// <summary>"Mark as sent" says the user sent the message themselves: Confirmed must be true, otherwise nothing changes.</summary>
public record MarkSentDto(bool? Confirmed);

public record BulkDraftsDto(Guid[]? LeadIds, string? Channel, string? Product);

public record PrefillDto(string Channel, string Subject, string Body, string Language, string? Product, string TemplateSource, string? ToAddress, List<string> Warnings);

public record QueueItemDto(
    Guid Id, string Name, int? Score, string? Label, string? Sector, string? City, string? OwnerName, string? WhyLead, string? Needs,
    IReadOnlyList<ProductFitDto>? ProductFit, bool HasDraft, DateTime? LastContactedAt);

public record UpsertTemplateDto(string? Channel, string? ProductKey, string? Subject, string? Body);
