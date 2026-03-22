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
    public DbSet<Client> Clients { get; set; }
    public DbSet<Project> Projects { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

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
    }
}
