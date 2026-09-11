using Microsoft.EntityFrameworkCore;
using PromoEngine.Domain.Catalog;
using PromoEngine.Infrastructure.Postgres;

namespace PromoEngine.Infrastructure.Catalog;

/// <summary>
/// The shared control plane database. Holds tenants, their users, the subscription
/// plan catalog and provisioning state. It never holds promotion data - that lives
/// in each tenant's own database.
/// </summary>
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantUser> Users => Set<TenantUser>();
    public DbSet<SubscriptionPlan> Plans => Set<SubscriptionPlan>();
    public DbSet<TenantSubscription> Subscriptions => Set<TenantSubscription>();

    /// <summary>Stores every DateTimeOffset as UTC, which is all timestamptz can hold.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder b) => b.UseUtcDateTimeOffsets();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddCaseInsensitiveCollation();

        b.Entity<Tenant>(e =>
        {
            e.ToTable("Tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(80).IsRequired();
            e.Property(x => x.CompanyName).HasMaxLength(200);
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.ContactPhone).HasMaxLength(50);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.CurrencyCode).HasMaxLength(3);
            e.Property(x => x.DatabaseName).HasMaxLength(128);
            e.Property(x => x.ConnectionString).HasMaxLength(1024);
            e.Property(x => x.ProvisionerName).HasMaxLength(32);
            e.Property(x => x.RdsInstanceIdentifier).HasMaxLength(128);
            e.Property(x => x.ProvisioningError).HasMaxLength(2000);
            e.Property(x => x.Email).HasConversion(v => v.ToLowerInvariant(), v => v);
            // Looked up by equality and uniquely indexed, which SQL Server did
            // case-insensitively. See PostgresConventions.
            e.Property(x => x.Email).UseCollation(PostgresConventions.CaseInsensitiveCollation);
            e.Property(x => x.Slug).UseCollation(PostgresConventions.CaseInsensitiveCollation);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<TenantUser>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired()
                .UseCollation(PostgresConventions.CaseInsensitiveCollation);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.PasswordSalt).HasMaxLength(512);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.Tenant).WithMany(t => t.Users)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SubscriptionPlan>(e =>
        {
            e.ToTable("SubscriptionPlans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(32).IsRequired()
                .UseCollation(PostgresConventions.CaseInsensitiveCollation);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Tagline).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.CurrencyCode).HasMaxLength(3);
            e.Property(x => x.AllowedTemplateCodes).HasMaxLength(2000);
            e.Property(x => x.MonthlyPrice).HasPrecision(18, 2);
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<TenantSubscription>(e =>
        {
            e.ToTable("TenantSubscriptions");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Tenant).WithMany(t => t.Subscriptions)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Plan).WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.Status });
        });
    }
}
