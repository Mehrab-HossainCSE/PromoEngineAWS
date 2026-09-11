using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PromoEngine.Api.Contracts;
using PromoEngine.Api.Middleware;
using PromoEngine.Api.Services;
using PromoEngine.Infrastructure.Catalog;

namespace PromoEngine.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        group.MapPost("/register", async (
            [FromBody] RegisterRequest request,
            TenantOnboardingService onboarding,
            CancellationToken ct) =>
        {
            var result = await onboarding.RegisterAsync(request, ct);

            return result.Success
                ? Results.Ok(result.Auth)
                : Results.Problem(title: "Registration failed", detail: result.Error, statusCode: StatusCodes.Status409Conflict);
        })
        .WithSummary("Create an account with email and password")
        .AllowAnonymous();

        group.MapPost("/login", async (
            [FromBody] LoginRequest request,
            TenantOnboardingService onboarding,
            CancellationToken ct) =>
        {
            var result = await onboarding.LoginAsync(request, ct);

            return result.Success
                ? Results.Ok(result.Auth)
                : Results.Problem(title: "Sign in failed", detail: result.Error, statusCode: StatusCodes.Status401Unauthorized);
        })
        .WithSummary("Sign in")
        .AllowAnonymous();

        group.MapPost("/guest", (TenantOnboardingService onboarding) =>
            Results.Ok(onboarding.StartGuestSession()))
        .WithSummary("Continue as a guest")
        .WithDescription("Issues a short lived token that can browse plans and offer templates. Promotion data requires an account.")
        .AllowAnonymous();

        group.MapPost("/guest/convert", async (
            [FromBody] GuestConversionRequest request,
            TenantOnboardingService onboarding,
            CancellationToken ct) =>
        {
            var result = await onboarding.ConvertGuestAsync(request, ct);

            return result.Success
                ? Results.Ok(result.Auth)
                : Results.Problem(title: "Could not create your workspace", detail: result.Error, statusCode: StatusCodes.Status409Conflict);
        })
        .WithSummary("Collect guest customer details and provision their workspace")
        .WithDescription("Called when a guest picks a plan. Creates the tenant, records the subscription and queues the dedicated database.")
        .AllowAnonymous();

        group.MapGet("/me", async (
            ICurrentUser currentUser,
            CatalogDbContext catalog,
            TenantOnboardingService onboarding,
            CancellationToken ct) =>
        {
            if (currentUser.IsGuest)
            {
                return Results.Ok(new MeResponse { IsGuest = true });
            }

            if (currentUser.UserId is not { } userId)
            {
                return Results.Unauthorized();
            }

            var user = await catalog.Users
                .Include(u => u.Tenant)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user?.Tenant is null)
            {
                return Results.Unauthorized();
            }

            var subscription = await onboarding.GetCurrentSubscriptionAsync(user.TenantId, ct);
            return Results.Ok(new MeResponse { IsGuest = false, User = user.ToProfile(user.Tenant, subscription) });
        })
        .WithSummary("Current session")
        .RequireAuthorization();
    }
}
