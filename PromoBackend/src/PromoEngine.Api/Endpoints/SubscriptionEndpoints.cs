using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PromoEngine.Api.Contracts;
using PromoEngine.Api.Middleware;
using PromoEngine.Api.Services;
using PromoEngine.Domain.Catalog;
using PromoEngine.Domain.Templates;
using PromoEngine.Infrastructure.Catalog;

namespace PromoEngine.Api.Endpoints;

public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/subscriptions").WithTags("Subscriptions");

        group.MapGet("/plans", async (CatalogDbContext catalog, CancellationToken ct) =>
        {
            var plans = await catalog.Plans
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.DisplayOrder)
                .ToListAsync(ct);

            return Results.Ok(plans.Select(p => p.ToResponse()).ToList());
        })
        .WithSummary("List subscription plans")
        .AllowAnonymous();

        group.MapPost("/subscribe", async (
            [FromBody] SubscribeRequest request,
            ICurrentUser currentUser,
            TenantOnboardingService onboarding,
            CancellationToken ct) =>
        {
            if (currentUser.IsGuest)
            {
                return Results.Problem(
                    title: "Customer details required",
                    detail: "Guests must supply their email, password and company details before a workspace can be created. Post to /api/auth/guest/convert instead.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (currentUser.TenantId is not { } tenantId)
            {
                return Results.Unauthorized();
            }

            var (success, error, subscription) = await onboarding.SubscribeAsync(tenantId, request.PlanCode, ct);

            return success
                ? Results.Ok(subscription)
                : Results.Problem(title: "Subscription failed", detail: error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithSummary("Subscribe the current tenant to a plan")
        .WithDescription("Records the subscription and queues creation of the tenant's dedicated AWS database.")
        .RequireAuthorization();

        group.MapGet("/status", async (
            ICurrentUser currentUser,
            CatalogDbContext catalog,
            TenantOnboardingService onboarding,
            CancellationToken ct) =>
        {
            if (currentUser.TenantId is not { } tenantId)
            {
                return Results.Unauthorized();
            }

            var tenant = await catalog.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            var subscription = await onboarding.GetCurrentSubscriptionAsync(tenantId, ct);

            return Results.Ok(new ProvisioningStatusResponse
            {
                TenantId = tenant.Id,
                Status = tenant.ProvisioningStatus.ToString(),
                IsReady = tenant.IsReady,
                DatabaseName = tenant.DatabaseName,
                Provisioner = tenant.ProvisionerName,
                Error = tenant.ProvisioningError,
                ProvisionedAtUtc = tenant.ProvisionedAtUtc,
                Subscription = subscription?.ToResponse()
            });
        })
        .WithSummary("Provisioning and subscription status")
        .WithDescription("Polled by the client while the tenant database is being created.")
        .RequireAuthorization();

        group.MapPost("/retry-provisioning", async (
            ICurrentUser currentUser,
            TenantOnboardingService onboarding,
            CancellationToken ct) =>
        {
            if (currentUser.TenantId is not { } tenantId)
            {
                return Results.Unauthorized();
            }

            var queued = await onboarding.RetryProvisioningAsync(tenantId, ct);

            return queued
                ? Results.Accepted()
                : Results.Problem(title: "Nothing to retry", detail: "The workspace is already provisioned.", statusCode: StatusCodes.Status400BadRequest);
        })
        .WithSummary("Retry a failed database provisioning")
        .RequireAuthorization();

        // Offer templates are reference data. Guests can browse them, which is what
        // makes the product explorable before signing up; the AvailableOnCurrentPlan
        // flag tells the UI which ones the caller can actually use.
        app.MapGet("/api/offer-templates", async (
            ICurrentUser currentUser,
            CatalogDbContext catalog,
            CancellationToken ct) =>
        {
            SubscriptionPlan? plan = null;

            if (!currentUser.IsGuest && currentUser.TenantId is { } tenantId)
            {
                plan = await catalog.Subscriptions
                    .Where(s => s.TenantId == tenantId && s.Status != SubscriptionStatus.Cancelled)
                    .OrderByDescending(s => s.CreatedAtUtc)
                    .Select(s => s.Plan)
                    .FirstOrDefaultAsync(ct);
            }

            var templates = OfferTemplateCatalog.All
                .Select(t => t.ToResponse(availableOnPlan: plan is null || IsAvailable(t, plan)))
                .ToList();

            return Results.Ok(templates);
        })
        .WithTags("Reference data")
        .WithSummary("Offer templates and the field rules each one implies")
        .RequireAuthorization();
    }

    private static bool IsAvailable(OfferTemplate template, SubscriptionPlan plan)
    {
        if (!plan.AllowsTemplate(template.Code))
        {
            return false;
        }

        if (template.Level == Domain.Promotions.OfferLevel.Transaction && !plan.AllowsTransactionLevelOffers)
        {
            return false;
        }

        return template.Type != Domain.Promotions.OfferType.GiftWithPurchase || plan.AllowsGiftWithPurchase;
    }
}
