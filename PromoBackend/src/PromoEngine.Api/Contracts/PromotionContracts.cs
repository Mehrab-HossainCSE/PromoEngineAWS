using System.ComponentModel.DataAnnotations;

namespace PromoEngine.Api.Contracts;

// ---------------------------------------------------------------------------
// Promotions
// ---------------------------------------------------------------------------

public record PromotionSummaryResponse
{
    public required Guid Id { get; init; }
    public required int PromotionNumber { get; init; }
    public required string Description { get; init; }
    public string? CampaignCode { get; init; }
    public required string Status { get; init; }
    public required int OfferCount { get; init; }
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public record PromotionDetailResponse : PromotionSummaryResponse
{
    public Guid? CampaignId { get; init; }
    public required IReadOnlyList<OfferResponse> Offers { get; init; }
}

public record SavePromotionRequest
{
    [Required, MaxLength(400)]
    public string Description { get; init; } = string.Empty;

    public Guid? CampaignId { get; init; }
}

/// <summary>
/// Search criteria. The Pricing guide requires at least one of promotion number,
/// description, offer description, start date or item.
/// </summary>
public record PromotionSearchRequest
{
    public int? PromotionNumber { get; init; }
    public string? Description { get; init; }
    public string? OfferDescription { get; init; }
    public string? OfferType { get; init; }
    public string? TemplateCode { get; init; }
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public string? Status { get; init; }
    public string? ItemId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public record PagedResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Total { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
}

// ---------------------------------------------------------------------------
// Offers
// ---------------------------------------------------------------------------

public record OfferResponse
{
    public required Guid Id { get; init; }
    public required Guid PromotionId { get; init; }
    public required int OfferNumber { get; init; }
    public required string Description { get; init; }
    public required string Level { get; init; }
    public required string Type { get; init; }
    public required string TemplateCode { get; init; }
    public required string TemplateName { get; init; }
    public required DateTimeOffset StartDate { get; init; }
    public TimeSpan? StartTime { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public TimeSpan? EndTime { get; init; }
    public string? Comments { get; init; }
    public string? CouponCode { get; init; }
    public required bool CouponCodeRequired { get; init; }
    public required string DistributionRule { get; init; }
    public required bool ExclusiveDiscount { get; init; }
    public string? CustomerDescription { get; init; }
    public required string Status { get; init; }
    public required bool IsEmergency { get; init; }
    public string? CancelReason { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAtUtc { get; init; }
    public required IReadOnlyList<ConditionResponse> Conditions { get; init; }
    public RewardResponse? Reward { get; init; }
    public required IReadOnlyList<OfferItemResponse> RewardItems { get; init; }
    public required IReadOnlyList<OfferLocationResponse> Locations { get; init; }

    /// <summary>The offer's uploaded barcode sheet, served to the point of sale.</summary>
    public required IReadOnlyList<OfferProductResponse> Products { get; init; }
}

public record ConditionResponse
{
    public required Guid Id { get; init; }
    public required int Sequence { get; init; }
    public decimal? BuyQuantity { get; init; }
    public string? UnitOfMeasure { get; init; }
    public decimal? SpendAmount { get; init; }
    public string? CurrencyCode { get; init; }
    public required string PriceRestriction { get; init; }
    public decimal? PriceRestrictionFrom { get; init; }
    public decimal? PriceRestrictionTo { get; init; }
    public string? PriceRestrictionCurrency { get; init; }
    public required IReadOnlyList<OfferItemResponse> Items { get; init; }
}

public record RewardResponse
{
    public required string DiscountType { get; init; }
    public decimal? DiscountValue { get; init; }
    public string? CurrencyCode { get; init; }
    public required bool AppliesToAllCurrencies { get; init; }
    public required string ApplyTo { get; init; }
    public int? ApplyDiscountUpTo { get; init; }
    public decimal? GetQuantity { get; init; }
    public string? GiftItemId { get; init; }
    public string? GiftItemDescription { get; init; }
    public string? SingleItemId { get; init; }
    public string? SingleItemDescription { get; init; }
}

public record OfferItemResponse
{
    public required Guid Id { get; init; }
    public required string Scope { get; init; }
    public required string Action { get; init; }
    public required string Level { get; init; }
    public required string Summary { get; init; }
    public string? Department { get; init; }
    public string? Class { get; init; }
    public string? Subclass { get; init; }
    public string? ItemId { get; init; }
    public string? ItemDescription { get; init; }
    public string? Barcode { get; init; }
    public string? StyleCode { get; init; }
    public string? VendorName { get; init; }
    public int? SourceRowNumber { get; init; }
    public string? ParentItemId { get; init; }
    public string? DiffType { get; init; }
    public string? DiffValue { get; init; }
    public string? ItemListId { get; init; }
    public string? SourceFileName { get; init; }
    public string? SupplierSite { get; init; }
    public string? Brand { get; init; }
    public string? CancelReason { get; init; }
    public DateTimeOffset? CancelledAtUtc { get; init; }
}

public record OfferLocationResponse
{
    public required Guid Id { get; init; }
    public required string Action { get; init; }
    public required string Level { get; init; }
    public required string Summary { get; init; }
    public string? ZoneGroup { get; init; }
    public string? Zone { get; init; }
    public string? LocationList { get; init; }
    public string? Store { get; init; }
    public string? StoreName { get; init; }
    public string? CancelReason { get; init; }
    public DateTimeOffset? CancelledAtUtc { get; init; }
}

// ---------------------------------------------------------------------------
// Write models
// ---------------------------------------------------------------------------

public record SaveOfferRequest
{
    [Required, MaxLength(400)]
    public string Description { get; init; } = string.Empty;

    [Required, MaxLength(64)]
    public string TemplateCode { get; init; } = string.Empty;

    [Required]
    public DateTimeOffset StartDate { get; init; }

    public TimeSpan? StartTime { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public TimeSpan? EndTime { get; init; }

    [MaxLength(2000)]
    public string? Comments { get; init; }

    [MaxLength(64)]
    public string? CouponCode { get; init; }

    public bool CouponCodeRequired { get; init; }

    /// <summary>BuyItems, GetItems or BothBuyAndGetItems. Ignored by templates that do not use it.</summary>
    public string? DistributionRule { get; init; }

    public bool ExclusiveDiscount { get; init; }

    [MaxLength(1000)]
    public string? CustomerDescription { get; init; }

    /// <summary>Bypasses the price event lead time rule. Requires a plan that allows it.</summary>
    public bool IsEmergency { get; init; }

    public List<SaveConditionRequest> Conditions { get; init; } = [];

    public SaveRewardRequest? Reward { get; init; }

    /// <summary>Discounted or excluded items shown on the rewards page.</summary>
    public List<SaveOfferItemRequest> RewardItems { get; init; } = [];

    /// <summary>
    /// The offer's own uploaded barcode sheet, which is what the external point of
    /// sale API answers from. Sent with the offer rather than uploaded separately so
    /// that a sheet and the discount it belongs to are saved in one go.
    /// </summary>
    public List<OfferProductRow> Products { get; init; } = [];
}

public record SaveConditionRequest
{
    public int Sequence { get; init; } = 1;
    public decimal? BuyQuantity { get; init; }

    [MaxLength(20)]
    public string? UnitOfMeasure { get; init; }

    public decimal? SpendAmount { get; init; }

    [MaxLength(3)]
    public string? CurrencyCode { get; init; }

    /// <summary>None, GreaterThan, LessThan or Between.</summary>
    public string? PriceRestriction { get; init; }

    public decimal? PriceRestrictionFrom { get; init; }
    public decimal? PriceRestrictionTo { get; init; }

    [MaxLength(3)]
    public string? PriceRestrictionCurrency { get; init; }

    public List<SaveOfferItemRequest> Items { get; init; } = [];
}

public record SaveRewardRequest
{
    /// <summary>PercentOff, AmountOff, FixedPrice or FreeItem.</summary>
    [Required]
    public string DiscountType { get; init; } = "PercentOff";

    public decimal? DiscountValue { get; init; }

    [MaxLength(3)]
    public string? CurrencyCode { get; init; }

    public bool AppliesToAllCurrencies { get; init; } = true;

    /// <summary>Regular, Clearance or RegularAndClearance.</summary>
    public string? ApplyTo { get; init; }

    public int? ApplyDiscountUpTo { get; init; }
    public decimal? GetQuantity { get; init; }

    [MaxLength(64)]
    public string? GiftItemId { get; init; }

    [MaxLength(400)]
    public string? GiftItemDescription { get; init; }

    [MaxLength(64)]
    public string? SingleItemId { get; init; }

    [MaxLength(400)]
    public string? SingleItemDescription { get; init; }
}

public record SaveOfferItemRequest
{
    /// <summary>Include or Exclude.</summary>
    public string Action { get; init; } = "Include";

    /// <summary>
    /// AllDepartments, Department, Class, Subclass, Item, ParentDiff, ItemList or
    /// SupplierSiteBrand. UploadList is still accepted so rules stored before the
    /// spreadsheet path moved to the offer's product sheet keep working, but nothing
    /// creates one any more.
    /// </summary>
    [Required]
    public string Level { get; init; } = "AllDepartments";

    public string? Department { get; init; }
    public string? Class { get; init; }
    public string? Subclass { get; init; }
    public string? ItemId { get; init; }
    public string? ItemDescription { get; init; }

    /// <summary>Barcode / UPC of the item, when a single item is selected.</summary>
    [MaxLength(64)]
    public string? Barcode { get; init; }

    /// <summary>Style code, typically supplied by an uploaded item list.</summary>
    [MaxLength(64)]
    public string? StyleCode { get; init; }

    /// <summary>Vendor / supplier name, typically supplied by an uploaded item list.</summary>
    [MaxLength(200)]
    public string? VendorName { get; init; }

    /// <summary>Row this item came from in the uploaded spreadsheet.</summary>
    public int? SourceRowNumber { get; init; }

    public string? ParentItemId { get; init; }
    public string? DiffType { get; init; }
    public string? DiffValue { get; init; }
    public string? ItemListId { get; init; }
    public string? SourceFileName { get; init; }
    public string? SupplierSite { get; init; }
    public string? Brand { get; init; }
}

public record SaveOfferLocationRequest
{
    public string Action { get; init; } = "Include";

    /// <summary>Zone, LocationList or Store.</summary>
    [Required]
    public string Level { get; init; } = "Store";

    public string? ZoneGroup { get; init; }
    public string? Zone { get; init; }
    public string? LocationList { get; init; }
    public string? Store { get; init; }
    public string? StoreName { get; init; }
}

public record CopyLocationsRequest
{
    [Required, MinLength(1)]
    public List<Guid> TargetOfferIds { get; init; } = [];
}

public record CreateOfferFromExistingRequest
{
    [Required, MaxLength(400)]
    public string Description { get; init; } = string.Empty;

    [Required]
    public DateTimeOffset StartDate { get; init; }

    public TimeSpan? StartTime { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public TimeSpan? EndTime { get; init; }
    public string? CouponCode { get; init; }
    public string? CustomerDescription { get; init; }
    public string? Comments { get; init; }

    /// <summary>Copy the source offer's locations onto the new offer.</summary>
    public bool CopyLocations { get; init; } = true;
}

public record MassUpdateOffersRequest
{
    [Required, MinLength(1)]
    public List<Guid> OfferIds { get; init; } = [];

    public DateTimeOffset? StartDate { get; init; }
    public TimeSpan? StartTime { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public TimeSpan? EndTime { get; init; }
    public string? CouponCode { get; init; }
    public string? Comments { get; init; }
    public string? CustomerDescription { get; init; }

    public bool ClearEndDate { get; init; }
    public bool ClearCouponCode { get; init; }
    public bool ClearComments { get; init; }
    public bool ClearCustomerDescription { get; init; }
}

public record CancelRequest
{
    [Required, MaxLength(500)]
    public string Reason { get; init; } = string.Empty;
}

public record CancelSelectionRequest : CancelRequest
{
    [Required, MinLength(1)]
    public List<Guid> Ids { get; init; } = [];
}

public record StatusChangeRequest
{
    [Required, MinLength(1)]
    public List<Guid> OfferIds { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Reference data
// ---------------------------------------------------------------------------

public record OfferTemplateResponse
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Level { get; init; }
    public required string Type { get; init; }
    public required string Example { get; init; }
    public required string Description { get; init; }
    public required string ConditionKind { get; init; }
    public required bool HasConditions { get; init; }
    public required bool AllowsMultipleConditions { get; init; }
    public required bool HasConditionItems { get; init; }
    public required bool ConditionPriceRestriction { get; init; }
    public required bool ConditionSingleItem { get; init; }
    public required string RewardKind { get; init; }
    public required string RewardItemMode { get; init; }
    public required bool RewardPriceRestriction { get; init; }
    public required bool AllowsFixedPrice { get; init; }
    public required bool AllowsApplyTo { get; init; }
    public required bool AllowsApplyDiscountUpTo { get; init; }
    public required bool AllowsDistributionRule { get; init; }

    /// <summary>False when the caller's plan does not include this template.</summary>
    public required bool AvailableOnCurrentPlan { get; init; }
}

public record CampaignResponse
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Description { get; init; }
}

public record SaveCampaignRequest
{
    [Required, MaxLength(10)]
    public string Code { get; init; } = string.Empty;

    [Required, MaxLength(1000)]
    public string Description { get; init; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Offer product upload - the one Excel Upload in the application
// ---------------------------------------------------------------------------


/// <summary>
/// One barcode row read out of a sheet uploaded against an offer.
///
/// There are no discount fields here on purpose: the offer's own reward is the
/// discount for every row it owns, so the sheet says which products take part and
/// nothing about what they are worth.
/// </summary>
public record OfferProductRow
{
    public required int RowNumber { get; init; }

    [Required, MaxLength(64)]
    public string Barcode { get; init; } = string.Empty;

    [MaxLength(64)] public string? ItemId { get; init; }
    [MaxLength(64)] public string? StyleCode { get; init; }
    [MaxLength(400)] public string? ItemDescription { get; init; }
    [MaxLength(64)] public string? Department { get; init; }
    [MaxLength(64)] public string? Class { get; init; }
    [MaxLength(64)] public string? Subclass { get; init; }
    [MaxLength(64)] public string? Brand { get; init; }
    [MaxLength(200)] public string? VendorName { get; init; }
    [MaxLength(64)] public string? SupplierSite { get; init; }

    /// <summary>
    /// Include or Exclude. An uploaded product is a discounted product unless the
    /// user says otherwise, so anything unrecognised - a blank cell above all -
    /// reads as Include.
    /// </summary>
    public string Action { get; init; } = "Include";

    /// <summary>The GetApplicablePromotions column: is this row served to the POS?</summary>
    public bool GetApplicablePromotions { get; init; } = true;

    [MaxLength(64)] public string? PromoTypeId { get; init; }
    [MaxLength(64)] public string? SiteCode { get; init; }
    [MaxLength(64)] public string? CustomerTier { get; init; }

    /// <summary>
    /// The workbook the row came from. Set by the parser and echoed back on save, so
    /// the user can drop one file's rows without disturbing another's.
    /// </summary>
    [MaxLength(260)] public string? SourceFileName { get; init; }
}

/// <summary>What the parse endpoint hands back to the offer wizard.</summary>
public record OfferProductUploadResponse
{
    public required string FileName { get; init; }
    public required bool Success { get; init; }
    public required int RowCount { get; init; }

    /// <summary>How many of the rows the external API will actually serve.</summary>
    public required int ApplicableRowCount { get; init; }

    /// <summary>True when the file had more rows than the configured cap.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Headings the parser recognised, so the user can see what was picked up.</summary>
    public required IReadOnlyList<string> RecognisedColumns { get; init; }

    /// <summary>
    /// False when the sheet had no GetApplicablePromotions column, in which case every
    /// row defaulted to being served. Surfaced so the UI can say so rather than imply
    /// the user supplied the flag.
    /// </summary>
    public required bool HasGetApplicablePromotionsColumn { get; init; }

    /// <summary>
    /// False when the sheet had no Include/Exclude column, in which case every row
    /// defaulted to Include - the uploaded products are the discounted products.
    /// </summary>
    public required bool HasActionColumn { get; init; }

    public required IReadOnlyList<OfferProductRow> Rows { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public string? Error { get; init; }
}

/// <summary>A stored offer product row, as served back with the offer.</summary>
public record OfferProductResponse
{
    public required Guid Id { get; init; }
    public required string Barcode { get; init; }
    public string? ItemId { get; init; }
    public string? StyleCode { get; init; }
    public string? ItemDescription { get; init; }
    public string? Department { get; init; }
    public string? Class { get; init; }
    public string? Subclass { get; init; }
    public string? Brand { get; init; }
    public string? VendorName { get; init; }
    public string? SupplierSite { get; init; }
    public required string Action { get; init; }
    public required bool GetApplicablePromotions { get; init; }
    public string? PromoTypeId { get; init; }
    public string? SiteCode { get; init; }
    public string? CustomerTier { get; init; }
    public string? SourceFileName { get; init; }
    public int? SourceRowNumber { get; init; }
    public string? UploadedBy { get; init; }
    public required DateTimeOffset UploadedAtUtc { get; init; }
}

// ---------------------------------------------------------------------------
// External applicable-promotions API
// ---------------------------------------------------------------------------

/// <summary>
/// Basket sent by a POS or commerce integration to discover promotions that can
/// be applied. Property names intentionally mirror the integration contract.
/// </summary>
public record GetApplicablePromotionsRequest
{
    /// <summary>
    /// Which customer's database to answer from. Accepts the tenant id, the tenant
    /// slug or the sign-in email - all three identify exactly one tenant, so an
    /// integrator can use whichever one they were given. Required: without it the
    /// endpoint has no way to know whose promotions are being asked about.
    /// </summary>
    [Required, MaxLength(320)]
    public string TenantId { get; init; } = string.Empty;

    [Required, MaxLength(64)]
    public string SiteCode { get; init; } = string.Empty;

    [MaxLength(50)]
    public string? MobileNo { get; init; }

    [MaxLength(64)]
    public string? CustomerTier { get; init; }

    [Required, MaxLength(64)]
    public string PromotypeID { get; init; } = string.Empty;

    [Required, MinLength(1)]
    public List<ApplicablePromotionItemRequest> Items { get; init; } = [];
}

public record ApplicablePromotionItemRequest
{
    [Required, MaxLength(64)]
    public string Barcode { get; init; } = string.Empty;

    [Range(0, double.MaxValue)]
    public decimal UnitPrice { get; init; }

    [Range(1, int.MaxValue)]
    public int InvoiceQty { get; init; }
}

public record ApplicablePromotionResponse
{
    public required int PromoId { get; init; }
    public required string PromoNo { get; init; }
    public required int SlabId { get; init; }
    public required string SlabNo { get; init; }
    public required string SlabDesc { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("IscrossCategory")]
    public required int IsCrossCategory { get; init; }

    public required IReadOnlyList<ApplicablePromotionOfferResponse> PromoOffers { get; init; }
}

public record ApplicablePromotionOfferResponse
{
    public required int OfferId { get; init; }
    public required string Offer { get; init; }
}
