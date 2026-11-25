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
    public DbSet<HuntJob> HuntJobs { get; set; }
    public DbSet<LeadMessage> LeadMessages { get; set; }
    public DbSet<MessageTemplate> MessageTemplates { get; set; }
    public DbSet<LeadDemoIdea> LeadDemoIdeas { get; set; }
    public DbSet<HuntCandidate> HuntCandidates { get; set; }

    // Organisations: leads are divided per organisation of ours, visibility follows membership
    public DbSet<Organization> Organizations { get; set; }
    public DbSet<OrganizationMember> OrganizationMembers { get; set; }
    public DbSet<OrganizationSeedLog> OrganizationSeedLog { get; set; }

    // Jengo Clients CRM core (task 8023)
    public DbSet<Contact> Contacts { get; set; }
    public DbSet<Interaction> Interactions { get; set; }
    public DbSet<NextAction> NextActions { get; set; }
    public DbSet<EntityLink> EntityLinks { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    public DbSet<CalendarFeed> CalendarFeeds { get; set; }
    public DbSet<ClientDocument> ClientDocuments { get; set; }
    public DbSet<ClientDocumentFile> ClientDocumentFiles { get; set; }
    public DbSet<ClientService> ClientServices { get; set; }
    public DbSet<ClientInvoice> ClientInvoices { get; set; }
    public DbSet<FedhaContactLink> FedhaContactLinks { get; set; }
    public DbSet<FedhaSyncRun> FedhaSyncRuns { get; set; }

    // Every save that touches a lead stamps UpdatedAt, whoever made the change (screen, import, enrichment, API).
    private void StampLeads()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Lead>())
        {
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = now;
            else if (entry.State == EntityState.Added && entry.Entity.UpdatedAt == default) entry.Entity.UpdatedAt = now;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampLeads();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampLeads();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

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

        builder.Entity<HuntJob>(e =>
        {
            e.Property(j => j.LeadIds).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<Guid>());
            e.HasIndex(j => new { j.UserId, j.CreatedAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(j => j.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            // At most one active job per user, enforced by the database, so two requests at the same moment cannot both start.
            e.HasIndex(j => j.UserId).IsUnique().HasFilter("\"Status\" IN ('Pending', 'Running')").HasDatabaseName("IX_HuntJobs_OneActivePerUser");
            e.HasMany(j => j.Candidates).WithOne(c => c.Job).HasForeignKey(c => c.JobId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<LeadMessage>(e =>
        {
            e.HasIndex(m => new { m.UserId, m.Status, m.UpdatedAt });
            e.HasIndex(m => m.LeadId);
            e.Property(m => m.Channel).HasMaxLength(20);
            e.Property(m => m.Status).HasMaxLength(10);
            e.HasOne(m => m.Lead).WithMany().HasForeignKey(m => m.LeadId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<LeadDemoIdea>(e =>
        {
            e.HasIndex(d => new { d.UserId, d.LeadId, d.Position });
            e.Property(d => d.Title).HasMaxLength(200);
            e.Property(d => d.ProductKey).HasMaxLength(20);
            e.Property(d => d.Model).HasMaxLength(80);
            e.HasOne(d => d.Lead).WithMany().HasForeignKey(d => d.LeadId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MessageTemplate>(e =>
        {
            // one template per user, channel and product key (an empty key is the default of the channel)
            e.HasIndex(t => new { t.UserId, t.Channel, t.ProductKey }).IsUnique();
            e.Property(t => t.Channel).HasMaxLength(20);
            e.Property(t => t.ProductKey).HasMaxLength(20);
        });
        builder.Entity<HuntCandidate>(e =>
        {
            e.HasIndex(c => c.LeadId);
            e.HasIndex(c => c.JobId);
            // A deleted lead leaves its candidate rows in the job history, without the link and without its evidence (see HuntEvidence.ForgetAsync).
            e.HasOne<Lead>().WithMany().HasForeignKey(c => c.LeadId).OnDelete(DeleteBehavior.SetNull);
        });

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

        // Unique index scoped per organisation for deduplication (the same business may be a lead of two organisations)
        builder.Entity<Lead>()
            .HasIndex(l => new { l.Name, l.Website, l.OrganizationId })
            .IsUnique();

        builder.Entity<Lead>()
            .HasOne(l => l.Organization).WithMany().HasForeignKey(l => l.OrganizationId).OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Organization>(e =>
        {
            e.HasIndex(o => o.Slug).IsUnique();
            e.Property(o => o.Name).HasMaxLength(100);
            e.Property(o => o.Slug).HasMaxLength(100);
            e.HasData(OrganizationSeed.All());
        });
        builder.Entity<OrganizationMember>(e =>
        {
            e.HasOne(m => m.Organization).WithMany().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.OrganizationId, m.Email }).IsUnique();
            e.HasIndex(m => m.UserId);
            e.Property(m => m.Email).HasMaxLength(254);
            e.Property(m => m.Role).HasMaxLength(10);
        });
        builder.Entity<OrganizationSeedLog>(e => e.HasKey(x => x.Key));

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
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProjectId);
            e.Property(x => x.Kind).HasConversion<string>();
        });

        builder.Entity<ClientDocument>(e =>
        {
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ClientDocument>().WithMany().HasForeignKey(x => x.PredecessorId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.HasIndex(x => new { x.ClientId, x.DocumentDate });
            e.HasIndex(x => x.PredecessorId);
            e.HasOne<ClientService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.ServiceId);
            e.HasIndex(x => new { x.ClientId, x.SourceRef });
        });

        builder.Entity<ClientService>(e =>
        {
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.MonthlyAmount).HasPrecision(14, 2);
            e.Property(x => x.BillingAmount).HasPrecision(14, 2);
            e.Property(x => x.HourlyRate).HasPrecision(14, 2);
            e.Property(x => x.VatRate).HasPrecision(5, 2);
            e.Property(x => x.BillingInterval).HasConversion<string>().HasDefaultValue(BillingInterval.Monthly);
            e.HasIndex(x => new { x.ClientId, x.Kind });
            e.HasIndex(x => new { x.ClientId, x.SourceRef });
        });

        builder.Entity<ClientInvoice>(e =>
        {
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ClientService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ClientDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.AmountExclVat).HasPrecision(14, 2);
            e.Property(x => x.VatRate).HasPrecision(5, 2);
            e.HasIndex(x => new { x.ClientId, x.InvoiceDate });
            e.HasIndex(x => x.ServiceId);
            e.HasIndex(x => new { x.ClientId, x.SourceRef });
        });

        builder.Entity<FedhaContactLink>(e =>
        {
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.FedhaContactId }).IsUnique();
        });

        builder.Entity<FedhaSyncRun>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.StartedAt });
        });

        builder.Entity<ClientDocumentFile>(e =>
        {
            e.HasOne(x => x.Document).WithMany(d => d.Files).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => x.StoragePath).IsUnique();
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
