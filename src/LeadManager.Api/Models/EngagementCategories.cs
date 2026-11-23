namespace LeadManager.Api.Models;

/// <summary>The engagement categories of a client: how much effort we put in and in what stage the relationship is.</summary>
public static class EngagementCategories
{
    public static readonly string[] All =
    {
        "Active", "Maintenance", "Reactive",
        "Prospect", "WarmLead", "OneOff", "Conflict", "RelationshipEnded", "Inactive",
    };

    /// <summary>Categories that never show up as "needs attention" on Today.</summary>
    public static readonly string?[] QuietCategories = { "Reactive", "Inactive", "RelationshipEnded", "Conflict" };
}
