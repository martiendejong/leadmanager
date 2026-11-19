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
    string? EngagementCategory,
    Guid? SourceLeadId,
    string? CreatedByUserId,
    DateTime CreatedAt,
    bool IsActive,
    List<ProjectDto> Projects,
    int? PriorityValue = null,
    int? PriorityUrgency = null,
    int? PriorityEffort = null,
    double Score = 0,
    DateTime? NextActionAt = null,
    DateTime? LastContactAt = null);

public record UpdatePriorityDto(int? Value, int? Urgency, int? Effort);

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
    string? Notes,
    string? EngagementCategory);

public record CreateClientProjectDto(string Name, string? Description, string? Status);

public record CreateClientDto(
    string Name,
    string? Plan,
    string? PrimaryContactName,
    string? PrimaryContactEmail,
    string? PrimaryContactPhone,
    string? City,
    string? Sector,
    string? Website,
    string? Notes,
    string? EngagementCategory,
    List<CreateClientProjectDto>? Projects = null);
