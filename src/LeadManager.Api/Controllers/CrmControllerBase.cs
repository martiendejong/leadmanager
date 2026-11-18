using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadManager.Api.Controllers;

/// <summary>Shared helpers for the Jengo Clients CRM controllers (task 8023).</summary>
[ApiController]
[Authorize]
[AllowApiKey]
public abstract class CrmControllerBase : ControllerBase
{
    protected readonly CrmParentAccess Access;

    protected CrmControllerBase(CrmParentAccess access) => Access = access;

    protected string? GetCurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    /// <summary>Verifies the caller owns the lead or client named in the route.</summary>
    protected Task<CrmParent?> ResolveParentAsync(string parentType, Guid parentId) =>
        Access.ResolveAsync(parentType, parentId, GetCurrentUserId());

    /// <summary>Verifies ownership for an entity that stores LeadId / ClientId.</summary>
    protected Task<CrmParent?> ResolveOwnerAsync(Guid? leadId, Guid? clientId) =>
        leadId.HasValue
            ? Access.ResolveAsync("leads", leadId.Value, GetCurrentUserId())
            : clientId.HasValue
                ? Access.ResolveAsync("clients", clientId.Value, GetCurrentUserId())
                : Task.FromResult<CrmParent?>(null);
}
