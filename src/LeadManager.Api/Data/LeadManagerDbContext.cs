using LeadManager.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Data;

public class LeadManagerDbContext : IdentityDbContext<ApplicationUser>
{
    public LeadManagerDbContext(DbContextOptions<LeadManagerDbContext> options) : base(options)
    {
    }

    public DbSet<Lead> Leads { get; set; }
    public DbSet<EnrichmentJob> EnrichmentJobs { get; set; }
    public DbSet<LeadPageContent> LeadPageContents { get; set; }
    public DbSet<LeadDocumentChunk> LeadDocumentChunks { get; set; }
    public DbSet<CompanyProfile> CompanyProfiles { get; set; }
    public DbSet<LeadActivity> Activities { get; set; }
    public DbSet<Client> Clients { get; set; }
    public DbSet<Project> Projects { get; set; }
    public DbSet<Notification> Notifications { get; set; }
    public DbSet<ClientIntake> ClientIntakes { get; set; }
    public DbSet<ClientBundle> ClientBundles { get; set; }
    public DbSet<SalesSettings> SalesSettings { get; set; }
    public DbSet<LeadNote> LeadNotes { get; set; }

    // Jengo Clients CRM core (task 8023)
    public DbSet<Contact> Contacts { get; set; }
    public DbSet<Interaction> Interactions { get; set; }
    public DbSet<NextAction> NextActions { get; set; }
    public DbSet<EntityLink> EntityLinks { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    public DbSet<CalendarFeed> CalendarFeeds { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureCrm(builder);

        builder.Entity<EnrichmentJob>()
            .Property(e => e.LeadIds)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<Guid>()
            );

        // Seed roles with fixed GUIDs so they don't change on every migration
        builder.Entity<IdentityRole>().HasData(
            new IdentityRole
            {
                Id = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
                Name = "Admin",
                NormalizedName = "ADMIN",
                ConcurrencyStamp = "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
            },
            new IdentityRole
            {
                Id = "b2c3d4e5-f6a7-8901-bcde-f12345678901",
                Name = "User",
                NormalizedName = "USER",
                ConcurrencyStamp = "b2c3d4e5-f6a7-8901-bcde-f12345678901"
            }
        );

        // Unique index scoped per user for deduplication
        builder.Entity<Lead>()
            .HasIndex(l => new { l.Name, l.Website, l.ImportedByUserId })
            .IsUnique();

        builder.Entity<LeadPageContent>()
            .HasOne(p => p.Lead)
            .WithMany()
            .HasForeignKey(p => p.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<LeadDocumentChunk>()
            .HasOne(c => c.Lead)
            .WithMany()
            .HasForeignKey(c => c.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<LeadDocumentChunk>()
            .HasOne(c => c.PageContent)
            .WithMany(p => p.Chunks)
            .HasForeignKey(c => c.PageContentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<CompanyProfile>()
            .HasIndex(p => p.UserId)
            .IsUnique();

        builder.Entity<LeadActivity>()
            .HasOne(a => a.Lead)
            .WithMany(l => l.Activities)
            .HasForeignKey(a => a.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<LeadNote>()
            .HasOne(n => n.Lead)
            .WithMany()
            .HasForeignKey(n => n.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<LeadNote>()
            .HasOne(n => n.CreatedBy)
            .WithMany()
            .HasForeignKey(n => n.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>
    /// CRM entities hang off exactly one parent (lead XOR client), enforced by a check
    /// constraint, and are deleted together with that parent.
    /// </summary>
    private static void ConfigureCrm(ModelBuilder builder)
    {
        const string oneParent = "(\"LeadId\" IS NULL) <> (\"ClientId\" IS NULL)";

        builder.Entity<Contact>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("CK_Contacts_OneParent", oneParent));
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.LeadId);
            e.HasIndex(x => x.ClientId);
            e.HasIndex(x => x.Email);
        });

        builder.Entity<Interaction>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("CK_Interactions_OneParent", oneParent));
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.ProjectId, x.OccurredAt });
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.Direction).HasConversion<string>();
            e.HasIndex(x => new { x.LeadId, x.OccurredAt });
            e.HasIndex(x => new { x.ClientId, x.OccurredAt });
            // Idempotent imports: the same source item cannot be logged twice per parent.
            // Two indexes, because NULLs are distinct in SQLite unique indexes: a combined
            // (LeadId, ClientId, SourceRef) index would never reject anything.
            e.HasIndex(x => new { x.LeadId, x.SourceRef }).IsUnique()
                .HasFilter("\"LeadId\" IS NOT NULL AND \"SourceRef\" IS NOT NULL");
            e.HasIndex(x => new { x.ClientId, x.SourceRef }).IsUnique()
                .HasFilter("\"ClientId\" IS NOT NULL AND \"SourceRef\" IS NOT NULL");
        });

        builder.Entity<NextAction>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("CK_NextActions_OneParent", oneParent));
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.AssignedToUserId, x.DoneAt, x.DueAt });
        });

        builder.Entity<EntityLink>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("CK_EntityLinks_OneParent", oneParent));
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Kind).HasConversion<string>();
        });

        builder.Entity<CalendarFeed>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId).IsUnique();
        });

        builder.Entity<ApiKey>(e =>
        {
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });
    }
}
