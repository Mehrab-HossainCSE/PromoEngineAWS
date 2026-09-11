using System.ComponentModel.DataAnnotations;

namespace PromoEngine.Api.Contracts;

public record PlanResponse
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Tagline { get; init; }
    public required string Description { get; init; }
    public required decimal MonthlyPrice { get; init; }
    public required string CurrencyCode { get; init; }
    public required int MaxPromotions { get; init; }
    public required int MaxOffersPerPromotion { get; init; }
    public required int MaxUsers { get; init; }
    public required bool AllowsTransactionLevelOffers { get; init; }
    public required bool AllowsGiftWithPurchase { get; init; }
    public required bool AllowsEmergencyOffers { get; init; }
    public required bool AllowsSpreadsheetUpload { get; init; }
    public required bool IsHighlighted { get; init; }

    /// <summary>Template codes this plan unlocks. Empty means every template.</summary>
    public required IReadOnlyList<string> AllowedTemplateCodes { get; init; }

    /// <summary>Bullet points rendered on the pricing card.</summary>
    public required IReadOnlyList<string> Features { get; init; }
}

public record SubscribeRequest
{
    [Required, MaxLength(32)]
    public string PlanCode { get; init; } = string.Empty;
}

public record SubscriptionResponse
{
    public required Guid Id { get; init; }
    public required string PlanCode { get; init; }
    public required string PlanName { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset StartsAtUtc { get; init; }
    public DateTimeOffset? TrialEndsAtUtc { get; init; }
    public required bool IsActive { get; init; }
}

public record ProvisioningStatusResponse
{
    public required Guid TenantId { get; init; }
    public required string Status { get; init; }
    public required bool IsReady { get; init; }
    public string? DatabaseName { get; init; }
    public string? Provisioner { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? ProvisionedAtUtc { get; init; }
    public SubscriptionResponse? Subscription { get; init; }
}
