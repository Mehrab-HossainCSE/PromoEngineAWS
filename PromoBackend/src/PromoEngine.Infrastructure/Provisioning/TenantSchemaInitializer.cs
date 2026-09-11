using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PromoEngine.Domain.Promotions;
using PromoEngine.Infrastructure.Tenancy;

namespace PromoEngine.Infrastructure.Provisioning;

/// <summary>
/// Applies the promotion schema to a freshly created tenant database and optionally
/// seeds the reference data a new customer needs to start building offers.
/// Shared by every provisioner so local and AWS tenants get identical schemas.
/// </summary>
public sealed class TenantSchemaInitializer(
    ITenantDbContextFactory contextFactory,
    TenantSchemaReconciler reconciler,
    ILogger<TenantSchemaInitializer> logger)
{
    public async Task InitializeAsync(string connectionString, bool seedSampleData, CancellationToken ct = default)
    {
        await using var db = contextFactory.CreateForConnectionString(connectionString);

        // EnsureCreated builds the schema from the model for a database that does not
        // exist yet. Switch to db.Database.MigrateAsync() once the tenant schema starts
        // versioning through EF migrations.
        var created = await db.Database.EnsureCreatedAsync(ct);

        if (!created)
        {
            // The database was already there, so it may predate recent model changes.
            await reconciler.ReconcileAsync(connectionString, ct);
        }

        if (!seedSampleData)
        {
            return;
        }

        if (await db.Campaigns.AnyAsync(ct))
        {
            return;
        }

        db.Campaigns.AddRange(
            new Campaign { Code = "SPRING", Description = "Spring seasonal campaign" },
            new Campaign { Code = "BTS", Description = "Back to school campaign" },
            new Campaign { Code = "HOLIDAY", Description = "Holiday campaign" });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded reference data into tenant database");
    }
}
