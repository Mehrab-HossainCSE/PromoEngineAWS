using Microsoft.EntityFrameworkCore;
using PromoEngine.Api.Middleware;
using PromoEngine.Domain.Catalog;
using PromoEngine.Infrastructure.Catalog;
using PromoEngine.Infrastructure.Tenancy;

namespace PromoEngine.Api.Services;

public sealed record TenantAccess(Tenant Tenant, SubscriptionPlan Plan, TenantSubscription Subscription);

/// <summary>
/// Gate in front of every promotion endpoint. A caller reaches promotion data only
/// when they are a real user (not a guest), their tenant database finished
/// provisioning, and they hold an active subscription - which is what makes the
/// promotion features "unlocked by the selected subscription".
/// </summary>
public sealed class TenantAccessService(
    CatalogDbContext catalog,
    ICurrentUser currentUser,
    ITenantContext tenantContext)
{
    public async Task<(TenantAccess? Access, IResult? Problem)> ResolveAsync(CancellationToken ct)
    {
        if (currentUser.IsGuest)
        {
            return (null, Results.Problem(
                title: "Guest session",
                detail: "Create an account and choose a subscription to build promotions. Guests can browse plans and offer templates only.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (currentUser.TenantId is not { } tenantId)
        {
            return (null, Results.Unauthorized());
        }

        var tenant = await catalog.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            return (null, Results.Problem(
                title: "Tenant not found", statusCode: StatusCodes.Status404NotFound));
        }

        var subscription = await catalog.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId && s.Status != SubscriptionStatus.Cancelled)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (subscription?.Plan is null)
        {
            return (null, Results.Problem(
                title: "No subscription",
                detail: "Choose a subscription plan to unlock promotion creation.",
                statusCode: StatusCodes.Status402PaymentRequired));
        }

        if (!tenant.IsReady)
        {
            return (null, Results.Problem(
                title: "Workspace not ready",
                detail: tenant.ProvisioningStatus == ProvisioningStatus.Failed
                    ? $"Your database could not be created: {tenant.ProvisioningError}"
                    : "Your database is still being created. This usually takes a few seconds.",
                statusCode: StatusCodes.Status409Conflict));
        }

        if (!subscription.IsCurrentlyActive)
        {
            return (null, Results.Problem(
                title: "Subscription inactive",
                detail: $"Your subscription is {subscription.Status}.",
                statusCode: StatusCodes.Status402PaymentRequired));
        }

        // The middleware already loaded the tenant, but a token minted before
        // provisioning finished would carry no connection string, so refresh it here.
        if (!tenantContext.HasTenant)
        {
            tenantContext.Set(tenant);
        }

        return (new TenantAccess(tenant, subscription.Plan, subscription), null);
    }
}
