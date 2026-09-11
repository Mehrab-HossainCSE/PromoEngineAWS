using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PromoEngine.Domain.Catalog;
using PromoEngine.Infrastructure.Catalog;

namespace PromoEngine.Infrastructure.Provisioning;

/// <summary>
/// Hands tenant ids to the background provisioner. Subscribing returns immediately and
/// the client polls the provisioning status, which matters because a dedicated RDS
/// instance takes minutes to come up.
/// </summary>
public interface ITenantProvisioningQueue
{
    ValueTask EnqueueAsync(Guid tenantId, CancellationToken ct = default);
}

public sealed class TenantProvisioningQueue : ITenantProvisioningQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ValueTask EnqueueAsync(Guid tenantId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(tenantId, ct);

    internal IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Drains the provisioning queue, creating the database for each tenant and writing
/// the resulting connection string and status back to the catalog.
/// </summary>
public sealed class TenantProvisioningWorker(
    TenantProvisioningQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<TenantProvisioningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var tenantId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProvisionAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled failure provisioning tenant {TenantId}", tenantId);
            }
        }
    }

    private async Task ProvisionAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var provisioner = scope.ServiceProvider.GetRequiredService<ITenantProvisioner>();

        var tenant = await catalog.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            logger.LogWarning("Tenant {TenantId} disappeared before provisioning", tenantId);
            return;
        }

        if (tenant.ProvisioningStatus == ProvisioningStatus.Ready)
        {
            return;
        }

        tenant.ProvisioningStatus = ProvisioningStatus.Provisioning;
        tenant.ProvisioningError = null;
        await catalog.SaveChangesAsync(ct);

        var result = await provisioner.ProvisionAsync(tenant, ct);

        if (result.Success)
        {
            tenant.ProvisioningStatus = ProvisioningStatus.Ready;
            tenant.DatabaseName = result.DatabaseName;
            tenant.ConnectionString = result.ConnectionString;
            tenant.RdsInstanceIdentifier = result.InstanceIdentifier;
            tenant.ProvisionerName = provisioner.Name;
            tenant.ProvisionedAtUtc = DateTimeOffset.UtcNow;

            // Activate the subscription that triggered provisioning now that the
            // tenant actually has somewhere to store promotions.
            var pending = await catalog.Subscriptions
                .Where(s => s.TenantId == tenant.Id && s.Status == SubscriptionStatus.Pending)
                .ToListAsync(ct);

            foreach (var subscription in pending)
            {
                subscription.Status = SubscriptionStatus.Active;
            }

            logger.LogInformation("Tenant {Email} is ready on database {Database}", tenant.Email, tenant.DatabaseName);
        }
        else
        {
            tenant.ProvisioningStatus = ProvisioningStatus.Failed;
            tenant.ProvisioningError = result.Error;
            logger.LogError("Provisioning failed for tenant {Email}: {Error}", tenant.Email, result.Error);
        }

        await catalog.SaveChangesAsync(ct);
    }
}
