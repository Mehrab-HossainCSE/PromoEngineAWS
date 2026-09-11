namespace PromoEngine.Domain.Promotions;

/// <summary>
/// A temporary price reduction for one or more items at one or more stores for a
/// defined amount of time. A promotion is a container for offers, and may optionally
/// be linked to a marketing campaign.
/// </summary>
public class Promotion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human readable sequential number shown in the UI.</summary>
    public int PromotionNumber { get; set; }

    public string Description { get; set; } = string.Empty;

    public Guid? CampaignId { get; set; }
    public Campaign? Campaign { get; set; }

    public PromotionStatus Status { get; set; } = PromotionStatus.Worksheet;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public List<Offer> Offers { get; set; } = [];

    /// <summary>Earliest start date across the offers, kept for search and display.</summary>
    public DateTimeOffset? StartDate => Offers.Count == 0 ? null : Offers.Min(o => o.StartDate);

    /// <summary>Latest end date across the offers. Null when at least one offer is open ended.</summary>
    public DateTimeOffset? EndDate =>
        Offers.Count == 0 || Offers.Any(o => o.EndDate is null) ? null : Offers.Max(o => o.EndDate);
}

/// <summary>
/// Marketing campaign used to link related promotions together. Campaigns are
/// reference data, normally loaded from the merchandising system.
/// </summary>
public class Campaign
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Up to 10 characters, per the merchandising foundation data template.</summary>
    public string Code { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<Promotion> Promotions { get; set; } = [];
}
