using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PromoEngine.Domain.Catalog;
using PromoEngine.Infrastructure.Catalog;

namespace PromoEngine.Infrastructure.Provisioning;

/// <summary>
/// On start up, brings every provisioned tenant database in line with the current
/// model. Without this, a tenant created before a property was added keeps failing on
/// every read of that entity until someone patches it by hand.
///
/// It runs once, in the background, so a slow or unreachable tenant database delays
/// nothing else. Turn it off with Tenancy:AutoSyncSchemaOnStartup when tenant counts
/// grow large enough that a start up sweep is the wrong shape.
/// </summary>
public sealed class TenantSchemaSyncService(
    IServiceScopeFactory scopeFactory,
    IOptions<TenancyOptions> options,
    ILogger<TenantSchemaSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoSyncSchemaOnStartup)
        {
            return;
        }

        // Let the app finish starting before touching every tenant database.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var reconciler = scope.ServiceProvider.GetRequiredService<TenantSchemaReconciler>();

        List<Tenant> tenants;

        try
        {
            tenants = await catalog.Tenants
                .AsNoTracking()
                .Where(t => t.ProvisioningStatus == ProvisioningStatus.Ready && t.ConnectionString != null)
                .ToListAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read tenants for schema sync");
            return;
        }

        var changed = 0;

        foreach (var tenant in tenants)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                var result = await reconciler.ReconcileAsync(tenant.ConnectionString!, stoppingToken);

                if (result.ChangedAnything)
                {
                    changed++;
                    logger.LogWarning(
                        "Tenant {Email} schema updated: {Notes}",
                        tenant.Email, string.Join("; ", result.Notes));
                }
            }
            catch (Exception ex)
            {
                // One unreachable tenant must not stop the rest.
                logger.LogError(ex, "Schema sync failed for tenant {Email}", tenant.Email);
            }
        }

        logger.LogInformation(
            "Tenant schema sync complete: {Checked} checked, {Changed} updated", tenants.Count, changed);
    }
}
