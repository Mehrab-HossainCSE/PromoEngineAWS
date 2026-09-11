namespace PromoEngine.Domain.Catalog;

public enum SubscriptionStatus
{
    Pending = 0,
    Active = 1,
    Expired = 2,
    Cancelled = 3
}

public class TenantSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid PlanId { get; set; }
    public SubscriptionPlan? Plan { get; set; }

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Pending;

    public DateTimeOffset StartsAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndsAtUtc { get; set; }

    /// <summary>Free evaluation period granted on sign up.</summary>
    public DateTimeOffset? TrialEndsAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsCurrentlyActive =>
        Status == SubscriptionStatus.Active &&
        StartsAtUtc <= DateTimeOffset.UtcNow &&
        (EndsAtUtc is null || EndsAtUtc > DateTimeOffset.UtcNow);
}
