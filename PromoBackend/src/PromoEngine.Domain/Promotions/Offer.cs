namespace PromoEngine.Domain.Promotions;

/// <summary>
/// An offer defines the reward a customer receives and the conditions required to
/// earn it. The template determines which of conditions/rewards are required and
/// which fields are meaningful.
/// </summary>
public class Offer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PromotionId { get; set; }
    public Promotion? Promotion { get; set; }

    public int OfferNumber { get; set; }
    public string Description { get; set; } = string.Empty;

    public OfferLevel Level { get; set; } = OfferLevel.Item;
    public OfferType Type { get; set; } = OfferType.SimpleDiscount;

    /// <summary>Template code, for example BUY_X_GET_Y_FOR_DISCOUNT. See OfferTemplateCatalog.</summary>
    public string TemplateCode { get; set; } = string.Empty;

    public DateTimeOffset StartDate { get; set; }
    public TimeSpan? StartTime { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public TimeSpan? EndTime { get; set; }

    public string? Comments { get; set; }

    public string? CouponCode { get; set; }

    /// <summary>When set the customer must present the coupon for the discount to apply.</summary>
    public bool CouponCodeRequired { get; set; }

    public DistributionRule DistributionRule { get; set; } = DistributionRule.NotApplicable;

    /// <summary>This offer cannot be combined with other discounts.</summary>
    public bool ExclusiveDiscount { get; set; }

    /// <summary>Text printed on the receipt or shown on the web site.</summary>
    public string? CustomerDescription { get; set; }

    public PromotionStatus Status { get; set; } = PromotionStatus.Worksheet;

    /// <summary>
    /// Emergency offers bypass the Price Event Processing Days lead time rule and
    /// require the emergency offer privilege.
    /// </summary>
    public bool IsEmergency { get; set; }

    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public List<OfferCondition> Conditions { get; set; } = [];
    public OfferReward? Reward { get; set; }
    public List<OfferItem> Items { get; set; } = [];
    public List<OfferLocation> Locations { get; set; } = [];

    /// <summary>
    /// Barcode rows uploaded for this offer, which is what the external point of
    /// sale API answers a scanned barcode from.
    /// </summary>
    public List<OfferProduct> Products { get; set; } = [];
}

/// <summary>
/// A buy condition. Buy X and Y templates carry several numbered conditions, all of
/// which must be met; every other template carries exactly one.
/// </summary>
public class OfferCondition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OfferId { get; set; }
    public Offer? Offer { get; set; }

    /// <summary>1 based ordering of the condition within the offer.</summary>
    public int Sequence { get; set; } = 1;

    /// <summary>Quantity that must be purchased, for buy-quantity templates.</summary>
    public decimal? BuyQuantity { get; set; }

    public string? UnitOfMeasure { get; set; }

    /// <summary>Threshold amount that must be spent, for spend templates.</summary>
    public decimal? SpendAmount { get; set; }

    public string? CurrencyCode { get; set; }

    public PriceRestrictionOperator PriceRestriction { get; set; } = PriceRestrictionOperator.None;
    public decimal? PriceRestrictionFrom { get; set; }
    public decimal? PriceRestrictionTo { get; set; }
    public string? PriceRestrictionCurrency { get; set; }

    public List<OfferItem> Items { get; set; } = [];
}

/// <summary>The reward earned when every condition of the offer is satisfied.</summary>
public class OfferReward
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OfferId { get; set; }
    public Offer? Offer { get; set; }

    public DiscountType DiscountType { get; set; } = DiscountType.PercentOff;

    /// <summary>Percentage, amount off, or fixed price depending on <see cref="DiscountType"/>.</summary>
    public decimal? DiscountValue { get; set; }

    public string? CurrencyCode { get; set; }

    /// <summary>When true the amount applies in every currency rather than a single one.</summary>
    public bool AppliesToAllCurrencies { get; set; } = true;

    public ApplyToPriceType ApplyTo { get; set; } = ApplyToPriceType.RegularAndClearance;

    /// <summary>Maximum number of times the discount can be applied. Null means unlimited.</summary>
    public int? ApplyDiscountUpTo { get; set; }

    /// <summary>Quantity of reward items granted, for get-Y templates.</summary>
    public decimal? GetQuantity { get; set; }

    /// <summary>The free item granted by a gift with purchase offer.</summary>
    public string? GiftItemId { get; set; }
    public string? GiftItemDescription { get; set; }

    /// <summary>The single item for Buy X of Single Item for Discount.</summary>
    public string? SingleItemId { get; set; }
    public string? SingleItemDescription { get; set; }
}

/// <summary>
/// An include or exclude rule over the merchandise hierarchy. Rows attached to a
/// condition are qualifying (buy) items; rows attached to the reward are get or
/// excluded items.
/// </summary>
public class OfferItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OfferId { get; set; }
    public Offer? Offer { get; set; }

    /// <summary>Set when the row belongs to a specific buy condition.</summary>
    public Guid? ConditionId { get; set; }
    public OfferCondition? Condition { get; set; }

    public ItemScope Scope { get; set; } = ItemScope.Condition;
    public SelectionAction Action { get; set; } = SelectionAction.Include;
    public ItemSelectionLevel Level { get; set; } = ItemSelectionLevel.AllDepartments;

    public string? Department { get; set; }
    public string? Class { get; set; }
    public string? Subclass { get; set; }

    public string? ItemId { get; set; }
    public string? ItemDescription { get; set; }

    /// <summary>Scanned barcode / UPC for the item, when the selection is a single item.</summary>
    public string? Barcode { get; set; }

    /// <summary>Style code the item belongs to, as supplied on an uploaded item list.</summary>
    public string? StyleCode { get; set; }

    /// <summary>Vendor / supplier name, as supplied on an uploaded item list.</summary>
    public string? VendorName { get; set; }

    /// <summary>Row number this item came from in the uploaded spreadsheet.</summary>
    public int? SourceRowNumber { get; set; }

    public string? ParentItemId { get; set; }
    public string? DiffType { get; set; }
    public string? DiffValue { get; set; }

    public string? ItemListId { get; set; }
    public string? SourceFileName { get; set; }

    public string? SupplierSite { get; set; }
    public string? Brand { get; set; }

    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }

    /// <summary>Short label rendered in the items grid.</summary>
    public string Summary => Level switch
    {
        ItemSelectionLevel.AllDepartments => "All departments",
        ItemSelectionLevel.Department => $"Dept {Department}",
        ItemSelectionLevel.Class => $"Dept {Department} / Class {Class}",
        ItemSelectionLevel.Subclass => $"Dept {Department} / Class {Class} / Subclass {Subclass}",
        ItemSelectionLevel.Item => BuildItemSummary(),
        ItemSelectionLevel.ParentDiff => $"Parent {ParentItemId} / {DiffType} {DiffValue}",
        ItemSelectionLevel.ItemList => $"Item list {ItemListId}",
        ItemSelectionLevel.UploadList => $"Uploaded list {SourceFileName}",
        ItemSelectionLevel.SupplierSiteBrand => $"Supplier {SupplierSite} / Brand {Brand}",
        _ => Level.ToString()
    };

    /// <summary>
    /// Item rows carry more than an id once they arrive from an uploaded list, so the
    /// summary folds in whichever of barcode, style and vendor were supplied.
    /// </summary>
    private string BuildItemSummary()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Barcode)) parts.Add(Barcode);
        if (!string.IsNullOrWhiteSpace(StyleCode)) parts.Add($"Style {StyleCode}");
        if (!string.IsNullOrWhiteSpace(VendorName)) parts.Add(VendorName);

        var head = string.IsNullOrWhiteSpace(ItemId) && !string.IsNullOrWhiteSpace(Barcode)
            ? $"Item {Barcode}"
            : $"Item {ItemId}";

        return parts.Count == 0 ? head : $"{head} ({string.Join(" · ", parts)})";
    }
}

/// <summary>A store, zone, or location list the offer is active at.</summary>
public class OfferLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OfferId { get; set; }
    public Offer? Offer { get; set; }

    public SelectionAction Action { get; set; } = SelectionAction.Include;
    public LocationLevel Level { get; set; } = LocationLevel.Store;

    public string? ZoneGroup { get; set; }
    public string? Zone { get; set; }
    public string? LocationList { get; set; }
    public string? Store { get; set; }
    public string? StoreName { get; set; }

    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }

    public string Summary => Level switch
    {
        LocationLevel.Zone => $"Zone group {ZoneGroup} / Zone {Zone}",
        LocationLevel.LocationList => $"Location list {LocationList}",
        LocationLevel.Store => string.IsNullOrWhiteSpace(StoreName) ? $"Store {Store}" : $"{Store} - {StoreName}",
        _ => Level.ToString()
    };
}
