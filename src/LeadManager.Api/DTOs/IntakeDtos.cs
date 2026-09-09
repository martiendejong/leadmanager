using LeadManager.Api.Models;

namespace LeadManager.Api.DTOs;

public record CreateIntakeDto(
    Guid ClientId,
    IntakeProductType ProductType,
    IntakeBundleType BundleType,
    string Requirements,
    string? FirstTask,
    string? AdditionalNotes
);

public record UpdateIntakeDto(
    IntakeProductType? ProductType,
    IntakeBundleType? BundleType,
    string? Requirements,
    string? FirstTask,
    string? AdditionalNotes
);

public record ApproveIntakeDto(bool Approved, string? RejectionReason);

public record IntakeDto(
    Guid Id,
    Guid ClientId,
    string ClientName,
    IntakeProductType ProductType,
    IntakeBundleType BundleType,
    IntakeStatus Status,
    string Requirements,
    string? FirstTask,
    string? AdditionalNotes,
    decimal? EstimatedHours,
    decimal? EstimatedPrice,
    string? EstimationReasoning,
    DateTime? ApprovedAt,
    string? WorkQueueItemId,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record ClientBundleDto(
    Guid Id,
    Guid ClientId,
    IntakeBundleType BundleType,
    int TotalHours,
    decimal HoursUsed,
    decimal HoursRemaining,
    decimal MonthlyPrice,
    decimal HourlyRate,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    bool IsActive,
    bool IsOverBudget
);
