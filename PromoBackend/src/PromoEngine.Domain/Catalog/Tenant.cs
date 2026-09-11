namespace PromoEngine.Domain.Catalog;

/// <summary>
/// Lifecycle of the tenant's dedicated database.
/// </summary>
public enum ProvisioningStatus
{
    NotRequested = 0,
    Queued = 1,
    Provisioning = 2,
    Ready = 3,
    Failed = 4
}

public enum TenantOrigin
{
    /// <summary>Tenant created by a user who registered with email + password.</summary>
    Registered = 0,

    /// <summary>Tenant created out of a guest session once customer details were collected.</summary>
    GuestConversion = 1
}

/// <summary>
/// A customer of PromoEngine. Every tenant owns an isolated database that holds
/// its promotions, offers and locations. Tenant metadata itself lives in the
/// shared catalog database.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Email the tenant was created from - the natural key used to name the database.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Url/database safe identifier derived from <see cref="Email"/>, for example "jane-contoso-com".</summary>
    public string Slug { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string? ContactPhone { get; set; }
    public string? Country { get; set; }
    public string CurrencyCode { get; set; } = "USD";

    public TenantOrigin Origin { get; set; } = TenantOrigin.Registered;

    public ProvisioningStatus ProvisioningStatus { get; set; } = ProvisioningStatus.NotRequested;
    public string? ProvisioningError { get; set; }
    public DateTimeOffset? ProvisionedAtUtc { get; set; }

    /// <summary>Name of the physical database created for this tenant.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Connection string to the tenant database. Populated by the provisioner;
    /// in production this should be replaced by a secret reference (AWS Secrets Manager).
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Which provisioner produced the database - "Local" or "Aws".</summary>
    public string? ProvisionerName { get; set; }

    /// <summary>RDS instance identifier when the AWS provisioner was used.</summary>
    public string? RdsInstanceIdentifier { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<TenantUser> Users { get; set; } = [];
    public List<TenantSubscription> Subscriptions { get; set; } = [];

    public bool IsReady => ProvisioningStatus == ProvisioningStatus.Ready
                           && !string.IsNullOrWhiteSpace(ConnectionString);
}
