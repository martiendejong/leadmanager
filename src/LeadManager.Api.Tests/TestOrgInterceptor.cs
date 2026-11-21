using System.Security.Cryptography;
using System.Text;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LeadManager.Api.Tests;

/// <summary>
/// Test convenience for the many tests written before organisations existed: a lead (or hunt job) saved without an organisation gets
/// a private one per importing user, with that user as its admin. So "my leads" and "someone else's leads" keep meaning what they meant,
/// and a user of the test only sees the leads they imported. Tests about organisations set OrganizationId themselves, which this
/// leaves alone. Production code never relies on it: there a lead without an organisation violates the foreign key.
/// </summary>
public sealed class TestOrgInterceptor : SaveChangesInterceptor
{
    public static Guid OrgIdFor(string? userId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("test-org:" + (userId ?? "")));
        return new Guid(hash);
    }

    /// <summary>Makes <paramref name="userId"/> admin of their own private test organisation (the one their leads land in).</summary>
    public static Guid Join(DbContext db, string userId)
    {
        var orgId = OrgIdFor(userId);
        if (!db.Set<Organization>().AsNoTracking().Any(o => o.Id == orgId))
        {
            db.Set<Organization>().Add(new Organization { Id = orgId, Name = "Test " + userId, Slug = "test-" + orgId.ToString("N") });
            db.Set<OrganizationMember>().Add(new OrganizationMember { OrganizationId = orgId, UserId = userId, Email = userId + "@test.local", Role = OrganizationRoles.Admin });
            db.SaveChanges();
        }
        return orgId;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Fill(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Fill(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Fill(DbContext? db)
    {
        if (db == null) return;
        var wanted = new Dictionary<Guid, string?>();
        foreach (var e in db.ChangeTracker.Entries<Lead>().Where(e => e.State == EntityState.Added && e.Entity.OrganizationId == Guid.Empty))
        {
            e.Entity.OrganizationId = OrgIdFor(e.Entity.ImportedByUserId);
            wanted[e.Entity.OrganizationId] = e.Entity.ImportedByUserId;
        }
        foreach (var e in db.ChangeTracker.Entries<HuntJob>().Where(e => e.State == EntityState.Added && e.Entity.OrganizationId == Guid.Empty))
        {
            e.Entity.OrganizationId = OrgIdFor(e.Entity.UserId);
            wanted[e.Entity.OrganizationId] = e.Entity.UserId;
        }
        foreach (var (orgId, userId) in wanted)
        {
            if (db.Set<Organization>().Local.Any(o => o.Id == orgId) || db.Set<Organization>().AsNoTracking().Any(o => o.Id == orgId)) continue;
            db.Set<Organization>().Add(new Organization { Id = orgId, Name = "Test " + userId, Slug = "test-" + orgId.ToString("N") });
            if (!string.IsNullOrEmpty(userId))
                db.Set<OrganizationMember>().Add(new OrganizationMember { OrganizationId = orgId, UserId = userId, Email = userId + "@test.local", Role = OrganizationRoles.Admin });
        }
    }
}
