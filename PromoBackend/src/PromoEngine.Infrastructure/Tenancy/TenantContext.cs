using Microsoft.EntityFrameworkCore;
using PromoEngine.Domain.Catalog;

namespace PromoEngine.Infrastructure.Tenancy;

/// <summary>
/// Per-request information about the tenant the caller belongs to. Populated by the
/// tenant resolution middleware from the JWT, and consumed by
/// <see cref="ITenantDbContextFactory"/> to open the right database.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    string? TenantSlug { get; }
    string? ConnectionString { get; }
    bool IsGuest { get; }
    bool HasTenant { get; }

    void Set(Tenant tenant);
    void SetGuest();
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? TenantSlug { get; private set; }
    public string? ConnectionString { get; private set; }
    public bool IsGuest { get; private set; }

    public bool HasTenant => TenantId is not null && !string.IsNullOrWhiteSpace(ConnectionString);

    public void Set(Tenant tenant)
    {
        TenantId = tenant.Id;
        TenantSlug = tenant.Slug;
        ConnectionString = tenant.ConnectionString;
        IsGuest = false;
    }

    public void SetGuest() => IsGuest = true;
}

/// <summary>Opens a <see cref="TenantDbContext"/> against a specific tenant database.</summary>
public interface ITenantDbContextFactory
{
    /// <summary>Context for the tenant on the current request. Throws when there is none.</summary>
    TenantDbContext CreateForCurrentTenant();

    /// <summary>Context for an explicit connection string, used by the provisioner.</summary>
    TenantDbContext CreateForConnectionString(string connectionString);
}

public sealed class TenantDbContextFactory(ITenantContext tenantContext) : ITenantDbContextFactory
{
    public TenantDbContext CreateForCurrentTenant()
    {
        if (!tenantContext.HasTenant)
        {
            throw new InvalidOperationException(
                "No tenant database is available for this request. The caller must be signed in to a tenant with an active subscription.");
        }

        return CreateForConnectionString(tenantContext.ConnectionString!);
    }

    public TenantDbContext CreateForConnectionString(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;

        return new TenantDbContext(options);
    }
}
