namespace PromoEngine.Domain.Catalog;

public enum UserRole
{
    /// <summary>Full access inside the tenant, including approving offers.</summary>
    Administrator = 0,

    /// <summary>Can create and edit promotions but not approve them.</summary>
    Merchandiser = 1,

    /// <summary>Read only.</summary>
    Viewer = 2
}

public class TenantUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Administrator;
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAtUtc { get; set; }
}
