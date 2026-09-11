using Microsoft.EntityFrameworkCore;
using PromoEngine.Api.Contracts;
using PromoEngine.Domain.Catalog;
using PromoEngine.Infrastructure.Catalog;
using PromoEngine.Infrastructure.Provisioning;
using PromoEngine.Infrastructure.Security;

namespace PromoEngine.Api.Services;

public sealed record OnboardingResult(bool Success, string? Error, AuthResponse? Auth = null)
{
    public static OnboardingResult Fail(string error) => new(false, error);
    public static OnboardingResult Ok(AuthResponse auth) => new(true, null, auth);
}

/// <summary>
/// Owns the sign up, sign in and subscribe flows, including turning a guest session
/// into a real tenant once the customer supplies their details.
/// </summary>
public sealed class TenantOnboardingService(
    CatalogDbContext catalog,
    IPasswordHasher passwordHasher,
    IJwtTokenService tokens,
    ITenantProvisioningQueue provisioningQueue,
    ILogger<TenantOnboardingService> logger)
{
    /// <summary>
    /// Registers a user with email and password. The tenant record is created up
    /// front but the database is only provisioned once a subscription is chosen,
    /// so we never create infrastructure for someone who abandons the flow.
    /// </summary>
    public async Task<OnboardingResult> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (await catalog.Users.AnyAsync(u => u.Email == email, ct))
        {
            return OnboardingResult.Fail("An account with that email already exists.");
        }

        var slug = TenantDatabaseNaming.BuildSlug(email);
        if (await catalog.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            slug = $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
        }

        var tenant = new Tenant
        {
            Email = email,
            Slug = slug,
            CompanyName = string.IsNullOrWhiteSpace(request.CompanyName) ? email : request.CompanyName.Trim(),
            ContactName = request.DisplayName?.Trim() ?? email,
            Origin = TenantOrigin.Registered,
            ProvisioningStatus = ProvisioningStatus.NotRequested
        };

        var (hash, salt) = passwordHasher.Hash(request.Password);
        var user = new TenantUser
        {
            TenantId = tenant.Id,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = UserRole.Administrator
        };

        catalog.Tenants.Add(tenant);
        catalog.Users.Add(user);
        await catalog.SaveChangesAsync(ct);

        logger.LogInformation("Registered tenant {Slug} for {Email}", tenant.Slug, email);

        return OnboardingResult.Ok(BuildAuthResponse(user, tenant, null));
    }

    public async Task<OnboardingResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var user = await catalog.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user?.Tenant is null || !user.IsActive)
        {
            return OnboardingResult.Fail("Email or password is incorrect.");
        }

        if (!passwordHasher.Verify(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            return OnboardingResult.Fail("Email or password is incorrect.");
        }

        user.LastLoginAtUtc = DateTimeOffset.UtcNow;
        await catalog.SaveChangesAsync(ct);

        var subscription = await GetCurrentSubscriptionAsync(user.TenantId, ct);
        return OnboardingResult.Ok(BuildAuthResponse(user, user.Tenant, subscription));
    }

    /// <summary>A guest gets a short lived token and can browse plans and templates only.</summary>
    public AuthResponse StartGuestSession()
    {
        var (token, expires) = tokens.CreateGuestToken(Guid.NewGuid());
        return new AuthResponse { Token = token, ExpiresAt = expires, IsGuest = true };
    }

    /// <summary>
    /// Collects the customer information a guest did not supply up front, creates the
    /// tenant and user, records the chosen subscription and queues the database.
    /// This is the path described by "if the user does not provide their user
    /// information, require or collect user information".
    /// </summary>
    public async Task<OnboardingResult> ConvertGuestAsync(GuestConversionRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (await catalog.Users.AnyAsync(u => u.Email == email, ct))
        {
            return OnboardingResult.Fail("An account with that email already exists. Sign in instead.");
        }

        var plan = await catalog.Plans.FirstOrDefaultAsync(p => p.Code == request.PlanCode && p.IsActive, ct);
        if (plan is null)
        {
            return OnboardingResult.Fail($"Unknown subscription plan '{request.PlanCode}'.");
        }

        var slug = TenantDatabaseNaming.BuildSlug(email);
        if (await catalog.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            slug = $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
        }

        var tenant = new Tenant
        {
            Email = email,
            Slug = slug,
            CompanyName = request.CompanyName.Trim(),
            ContactName = request.ContactName.Trim(),
            ContactPhone = request.ContactPhone?.Trim(),
            Country = request.Country?.Trim(),
            CurrencyCode = string.IsNullOrWhiteSpace(request.CurrencyCode) ? "USD" : request.CurrencyCode.Trim().ToUpperInvariant(),
            Origin = TenantOrigin.GuestConversion,
            ProvisioningStatus = ProvisioningStatus.Queued
        };

        var (hash, salt) = passwordHasher.Hash(request.Password);
        var user = new TenantUser
        {
            TenantId = tenant.Id,
            Email = email,
            DisplayName = request.ContactName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = UserRole.Administrator
        };

        var subscription = new TenantSubscription
        {
            TenantId = tenant.Id,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Pending,
            TrialEndsAtUtc = DateTimeOffset.UtcNow.AddDays(30)
        };

        catalog.Tenants.Add(tenant);
        catalog.Users.Add(user);
        catalog.Subscriptions.Add(subscription);
        await catalog.SaveChangesAsync(ct);

        await provisioningQueue.EnqueueAsync(tenant.Id, ct);
        logger.LogInformation("Converted guest session into tenant {Slug} on plan {Plan}", tenant.Slug, plan.Code);

        subscription.Plan = plan;
        return OnboardingResult.Ok(BuildAuthResponse(user, tenant, subscription));
    }

    /// <summary>
    /// Subscribes an existing tenant to a plan and queues database provisioning.
    /// Changing plan later does not re-provision, the tenant keeps its database.
    /// </summary>
    public async Task<(bool Success, string? Error, SubscriptionResponse? Subscription)> SubscribeAsync(
        Guid tenantId, string planCode, CancellationToken ct)
    {
        var tenant = await catalog.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            return (false, "Tenant not found.", null);
        }

        var plan = await catalog.Plans.FirstOrDefaultAsync(p => p.Code == planCode && p.IsActive, ct);
        if (plan is null)
        {
            return (false, $"Unknown subscription plan '{planCode}'.", null);
        }

        var existing = await catalog.Subscriptions
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Pending))
            .ToListAsync(ct);

        foreach (var previous in existing)
        {
            previous.Status = SubscriptionStatus.Cancelled;
            previous.EndsAtUtc = DateTimeOffset.UtcNow;
        }

        var alreadyProvisioned = tenant.ProvisioningStatus == ProvisioningStatus.Ready;

        var subscription = new TenantSubscription
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            // A tenant that already has its database is active immediately; a new one
            // becomes active when the provisioning worker reports success.
            Status = alreadyProvisioned ? SubscriptionStatus.Active : SubscriptionStatus.Pending,
            TrialEndsAtUtc = DateTimeOffset.UtcNow.AddDays(30)
        };

        catalog.Subscriptions.Add(subscription);

        if (!alreadyProvisioned)
        {
            tenant.ProvisioningStatus = ProvisioningStatus.Queued;
            tenant.ProvisioningError = null;
        }

        await catalog.SaveChangesAsync(ct);

        if (!alreadyProvisioned)
        {
            await provisioningQueue.EnqueueAsync(tenant.Id, ct);
        }

        subscription.Plan = plan;
        return (true, null, subscription.ToResponse());
    }

    /// <summary>Re-queues a tenant whose database failed to provision.</summary>
    public async Task<bool> RetryProvisioningAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await catalog.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null || tenant.ProvisioningStatus == ProvisioningStatus.Ready)
        {
            return false;
        }

        tenant.ProvisioningStatus = ProvisioningStatus.Queued;
        tenant.ProvisioningError = null;
        await catalog.SaveChangesAsync(ct);
        await provisioningQueue.EnqueueAsync(tenant.Id, ct);
        return true;
    }

    public async Task<TenantSubscription?> GetCurrentSubscriptionAsync(Guid tenantId, CancellationToken ct) =>
        await catalog.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId && s.Status != SubscriptionStatus.Cancelled)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    private AuthResponse BuildAuthResponse(TenantUser user, Tenant tenant, TenantSubscription? subscription)
    {
        var (token, expires) = tokens.CreateUserToken(user, tenant);

        return new AuthResponse
        {
            Token = token,
            ExpiresAt = expires,
            IsGuest = false,
            User = user.ToProfile(tenant, subscription)
        };
    }
}
