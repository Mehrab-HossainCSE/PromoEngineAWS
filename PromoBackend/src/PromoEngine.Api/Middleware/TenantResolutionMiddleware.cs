using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PromoEngine.Domain.Catalog;
using PromoEngine.Infrastructure.Catalog;
using PromoEngine.Infrastructure.Security;
using PromoEngine.Infrastructure.Tenancy;

namespace PromoEngine.Api.Middleware;

/// <summary>
/// Reads the tenant claim off the authenticated principal and loads that tenant's
/// connection string into the request scoped <see cref="ITenantContext"/>. Everything
/// downstream that touches promotion data goes through this, so a request can only
/// ever reach the database of the tenant in its own token.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext, CatalogDbContext catalog)
    {
        var user = context.User;

        if (user.Identity?.IsAuthenticated == true)
        {
            if (string.Equals(user.FindFirstValue(PromoClaims.IsGuest), "true", StringComparison.OrdinalIgnoreCase))
            {
                tenantContext.SetGuest();
            }
            else if (Guid.TryParse(user.FindFirstValue(PromoClaims.TenantId), out var tenantId))
            {
                var tenant = await catalog.Tenants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == tenantId, context.RequestAborted);

                if (tenant is not null)
                {
                    tenantContext.Set(tenant);
                }
                else
                {
                    logger.LogWarning("Token referenced unknown tenant {TenantId}", tenantId);
                }
            }
        }

        await next(context);
    }
}

/// <summary>Convenience accessor for the signed in user and their entitlements.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    bool IsGuest { get; }
    Guid? UserId { get; }
    Guid? TenantId { get; }
    string Email { get; }
    string DisplayName { get; }
    string Role { get; }
    bool CanApprove { get; }
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public bool IsGuest => string.Equals(
        Principal?.FindFirstValue(PromoClaims.IsGuest), "true", StringComparison.OrdinalIgnoreCase);

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? Principal?.FindFirstValue("sub"), out var id)
            ? id
            : null;

    public Guid? TenantId =>
        Guid.TryParse(Principal?.FindFirstValue(PromoClaims.TenantId), out var id) ? id : null;

    public string Email =>
        Principal?.FindFirstValue(ClaimTypes.Email)
        ?? Principal?.FindFirstValue("email")
        ?? "unknown";

    public string DisplayName => Principal?.FindFirstValue(ClaimTypes.Name) ?? Email;

    public string Role => Principal?.FindFirstValue(ClaimTypes.Role) ?? nameof(UserRole.Viewer);

    public bool CanApprove => Role == nameof(UserRole.Administrator);
}
