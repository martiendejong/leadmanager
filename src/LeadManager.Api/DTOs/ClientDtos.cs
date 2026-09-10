namespace LeadManager.Api.DTOs;

public record ConvertLeadDto(
    string Name,
    string? Plan,
    string? PrimaryContactName,
    string? PrimaryContactEmail,
    string? PrimaryContactPhone,
    string? Notes);

public record ClientDto(
    Guid Id,
    string Name,
    string? Plan,
    string? PrimaryContactName,
    string? PrimaryContactEmail,
    string? PrimaryContactPhone,
    string? City,
    string? Sector,
    string? Website,
    string? Notes,
    Guid? SourceLeadId,
    string? CreatedByUserId,
    DateTime CreatedAt,
    bool IsActive,
    List<ProjectDto> Projects);

public record ProjectDto(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    DateTime CreatedAt);

public record UpdateClientDto(
    string Name,
    string? Plan,
    string? PrimaryContactName,
    string? PrimaryContactEmail,
    string? PrimaryContactPhone,
    string? City,
    string? Sector,
    string? Website,
    string? Notes);
