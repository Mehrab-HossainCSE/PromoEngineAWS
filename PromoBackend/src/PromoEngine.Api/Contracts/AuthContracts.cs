using System.ComponentModel.DataAnnotations;

namespace PromoEngine.Api.Contracts;

public record RegisterRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; init; } = string.Empty;

    [Required, MinLength(8), MaxLength(128)]
    public string Password { get; init; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; init; }

    [MaxLength(200)]
    public string? CompanyName { get; init; }
}

public record LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// Customer details collected before a guest session can be turned into a real tenant.
/// The email supplied here is what the tenant database is named after.
/// </summary>
public record GuestConversionRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; init; } = string.Empty;

    [Required, MinLength(8), MaxLength(128)]
    public string Password { get; init; } = string.Empty;

    [Required, MaxLength(200)]
    public string ContactName { get; init; } = string.Empty;

    [Required, MaxLength(200)]
    public string CompanyName { get; init; } = string.Empty;

    [MaxLength(50)]
    public string? ContactPhone { get; init; }

    [MaxLength(100)]
    public string? Country { get; init; }

    [MaxLength(3)]
    public string? CurrencyCode { get; init; }

    /// <summary>Plan the guest picked before being asked to identify themselves.</summary>
    [Required, MaxLength(32)]
    public string PlanCode { get; init; } = string.Empty;
}

public record AuthResponse
{
    public required string Token { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required bool IsGuest { get; init; }
    public UserProfileResponse? User { get; init; }
}

public record UserProfileResponse
{
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
    public required Guid TenantId { get; init; }
    public required string TenantSlug { get; init; }
    public required string CompanyName { get; init; }
    public required string ProvisioningStatus { get; init; }
    public string? DatabaseName { get; init; }
    public SubscriptionResponse? Subscription { get; init; }
}

public record MeResponse
{
    public required bool IsGuest { get; init; }
    public UserProfileResponse? User { get; init; }
}
