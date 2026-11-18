using LeadManager.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services;

public enum CrmParentKind { Lead, Client }

/// <summary>A verified parent (lead or client) the caller is allowed to touch.</summary>
public readonly record struct CrmParent(CrmParentKind Kind, Guid Id)
{
    public Guid? LeadId => Kind == CrmParentKind.Lead ? Id : null;
    public Guid? ClientId => Kind == CrmParentKind.Client ? Id : null;
    public string Type => Kind == CrmParentKind.Lead ? "leads" : "clients";
}

/// <summary>
/// Single place that decides whether a user may attach CRM data to a lead or client.
/// Follows the existing ownership rule: leads by ImportedByUserId, clients by CreatedByUserId.
/// </summary>
public class CrmParentAccess
{
    public const string RouteConstraint = "regex(^(leads|clients)$)";

    private readonly LeadManagerDbContext _db;

    public CrmParentAccess(LeadManagerDbContext db) => _db = db;

    public async Task<CrmParent?> ResolveAsync(string parentType, Guid parentId, string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        switch (parentType.ToLowerInvariant())
        {
            case "leads":
                return await _db.Leads.AnyAsync(l => l.Id == parentId && l.ImportedByUserId == userId)
                    ? new CrmParent(CrmParentKind.Lead, parentId)
                    : null;
            case "clients":
                return await _db.Clients.AnyAsync(c => c.Id == parentId && c.CreatedByUserId == userId)
                    ? new CrmParent(CrmParentKind.Client, parentId)
                    : null;
            default:
                return null;
        }
    }
}
