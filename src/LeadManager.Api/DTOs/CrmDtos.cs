using System.Text.Json.Serialization;
using LeadManager.Api.Services;

namespace LeadManager.Api.DTOs;

// Jengo Clients: CRM DTOs (task 8023)
// Every timestamp is UTC and goes through UtcDateTimeConverter: written with "Z", read as UTC when the
// caller sends no zone. Without it SQLite's Kind=Unspecified values reach the browser without a zone.

public record ContactDto(
    Guid Id, Guid? LeadId, Guid? ClientId, string Name, string? Role, string? Email,
    string? Phone, string? Mobile, string? LinkedInUrl, string? PreferredChannel,
    string? Notes, [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime CreatedAt);

public record UpsertContactDto(
    string Name, string? Role, string? Email, string? Phone, string? Mobile,
    string? LinkedInUrl, string? PreferredChannel, string? Notes);

public record InteractionDto(
    Guid Id, Guid? LeadId, Guid? ClientId, Guid? ContactId, string Kind, string Direction,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime OccurredAt,
    string Summary, string? Outcome, string? SourceRef,
    string? CreatedByUserId, [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime CreatedAt);

public record CreateInteractionDto(
    string Kind, string? Direction,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? OccurredAt,
    string Summary, string? Outcome, Guid? ContactId, string? SourceRef);

public record NextActionDto(
    Guid Id, Guid? LeadId, Guid? ClientId, Guid? ContactId, string Title,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime DueAt,
    string? AssignedToUserId,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? DoneAt,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime CreatedAt);

/// <summary>AssignedToUserId is accepted for compatibility but ignored: an action is always yours.</summary>
public record CreateNextActionDto(
    string Title, [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime DueAt,
    Guid? ContactId, string? AssignedToUserId);

public record UpdateNextActionDto(
    string? Title, [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? DueAt,
    string? AssignedToUserId);

/// <summary>A next action with enough parent context to start the call without opening the lead.</summary>
public record TodayActionDto(
    Guid Id, string ParentType, Guid ParentId, string ParentName, string? Phone,
    string Title, [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime DueAt, bool Overdue);

public record EntityLinkDto(
    Guid Id, Guid? LeadId, Guid? ClientId, string Kind, string Label, string? Url,
    string? ExternalId, [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime CreatedAt);

public record CreateEntityLinkDto(string Kind, string Label, string? Url, string? ExternalId);

/// <summary>One row of the unified history of a lead or client.</summary>
public record TimelineItemDto(
    string Source, Guid Id, string Kind, string? Direction,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime At,
    string Summary, string? Outcome, string? SourceRef);

public record CreateApiKeyDto(string Name);

public record ApiKeyDto(
    Guid Id, string Name, string Prefix,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime CreatedAt,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? LastUsedAt,
    [property: JsonConverter(typeof(UtcNullableDateTimeConverter))] DateTime? RevokedAt);

/// <summary>Returned once, at creation. The plain key is never stored.</summary>
public record CreatedApiKeyDto(Guid Id, string Name, string Prefix, string Key);

public record CrmMatchDto(
    string ParentType, Guid ParentId, string Name, string? Website, string? Email,
    string? Phone, string MatchedOn);

// ---- Import (task 8024) ----------------------------------------------------------------

public record ImportContactDto(string Name, string? Role, string? Email, string? Phone, string? Mobile);

public record ImportInteractionDto(
    string Kind, string? Direction, DateTime? OccurredAt, string Summary, string? Outcome, string? SourceRef);

public record ImportNextActionDto(string Title, DateTime DueAt);

public record ImportLeadDto(
    string Name, string? Website, string? Sector, string? City, string? Phone, string? Email,
    string? Source, List<ImportContactDto>? Contacts, List<ImportInteractionDto>? Interactions,
    List<ImportNextActionDto>? NextActions);

public record ImportRequestDto(List<ImportLeadDto> Leads);

public record ImportLeadResultDto(
    string Name, string Action, Guid? LeadId, int ContactsCreated, int InteractionsCreated,
    int InteractionsSkipped, int NextActionsCreated, List<string> Problems);

public record ImportReportDto(bool DryRun, int LeadsCreated, int LeadsMatched, List<ImportLeadResultDto> Leads);
