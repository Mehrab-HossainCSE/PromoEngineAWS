using PromoEngine.Domain.Promotions;

namespace PromoEngine.Domain.Templates;

/// <summary>What the customer has to do to qualify.</summary>
public enum ConditionKind
{
    /// <summary>Simple discount templates have no pre-condition.</summary>
    None = 0,

    /// <summary>Buy a quantity of qualifying items.</summary>
    BuyQuantity = 1,

    /// <summary>Spend a threshold amount on qualifying items.</summary>
    SpendAmount = 2
}

/// <summary>What the customer gets.</summary>
public enum RewardKind
{
    /// <summary>A percent off, amount off, or fixed price.</summary>
    Discount = 0,

    /// <summary>A free item (gift with purchase).</summary>
    GiftItem = 1
}

/// <summary>Meaning of the item list shown on the rewards page.</summary>
public enum RewardItemMode
{
    /// <summary>No item list on the rewards page - the discount lands on the buy items.</summary>
    None = 0,

    /// <summary>Items eligible to receive the discount (the "get" set).</summary>
    DiscountedItems = 1,

    /// <summary>Items excluded from a transaction level discount.</summary>
    ExcludedItems = 2
}

/// <summary>
/// Static description of one offer template. The API returns these to the client so
/// that a single generic wizard can render every template correctly, rather than the
/// UI hard coding thirteen variants.
/// </summary>
public sealed record OfferTemplate
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required OfferLevel Level { get; init; }
    public required OfferType Type { get; init; }
    public required string Example { get; init; }
    public required string Description { get; init; }

    public ConditionKind ConditionKind { get; init; } = ConditionKind.None;

    /// <summary>Buy X and Y templates carry two or more numbered conditions.</summary>
    public bool AllowsMultipleConditions { get; init; }

    /// <summary>Conditions page shows an include/exclude qualifying items grid.</summary>
    public bool HasConditionItems { get; init; }

    /// <summary>Conditions page offers the price band filter.</summary>
    public bool ConditionPriceRestriction { get; init; }

    /// <summary>Conditions page picks one specific item instead of an item grid.</summary>
    public bool ConditionSingleItem { get; init; }

    public RewardKind RewardKind { get; init; } = RewardKind.Discount;
    public RewardItemMode RewardItemMode { get; init; } = RewardItemMode.None;

    /// <summary>Rewards page offers the price band filter on the discounted items.</summary>
    public bool RewardPriceRestriction { get; init; }

    /// <summary>Fixed Price is offered alongside Percent Off and Amount Off.</summary>
    public bool AllowsFixedPrice { get; init; }

    /// <summary>The Apply To (regular / clearance) selector is shown.</summary>
    public bool AllowsApplyTo { get; init; } = true;

    /// <summary>The Apply Discount Up To limit is shown.</summary>
    public bool AllowsApplyDiscountUpTo { get; init; } = true;

    /// <summary>The Distribution Rule field on the offer details page is meaningful.</summary>
    public bool AllowsDistributionRule { get; init; }

    public bool HasConditions => ConditionKind != ConditionKind.None;
}

/// <summary>
/// The thirteen offer templates supported by Pricing, with the field behaviour
/// documented for each one in the promotions guide.
/// </summary>
public static class OfferTemplateCatalog
{
    public const string GetYForDiscount = "GET_Y_FOR_DISCOUNT";
    public const string BuyXGetDiscount = "BUY_X_GET_DISCOUNT";
    public const string SpendXGetDiscount = "SPEND_X_GET_DISCOUNT";
    public const string BuyXGetYForDiscount = "BUY_X_GET_Y_FOR_DISCOUNT";
    public const string SpendXGetYForDiscount = "SPEND_X_GET_Y_FOR_DISCOUNT";
    public const string BuyXOfSingleItemForDiscount = "BUY_X_OF_SINGLE_ITEM_FOR_DISCOUNT";
    public const string BuyXAndYGetDiscount = "BUY_X_AND_Y_GET_DISCOUNT";
    public const string BuyXAndYGetZForDiscount = "BUY_X_AND_Y_GET_Z_FOR_DISCOUNT";
    public const string BuyXGetGiftWithPurchase = "BUY_X_GET_GIFT_WITH_PURCHASE";
    public const string SpendXGetGiftWithPurchase = "SPEND_X_GET_GIFT_WITH_PURCHASE";
    public const string TransactionGetDiscount = "TXN_GET_DISCOUNT";
    public const string TransactionBuyXGetDiscount = "TXN_BUY_X_GET_DISCOUNT";
    public const string TransactionSpendXGetDiscount = "TXN_SPEND_X_GET_DISCOUNT";

    public static readonly IReadOnlyList<OfferTemplate> All =
    [
        new()
        {
            Code = GetYForDiscount,
            Name = "Get Y for Discount",
            Level = OfferLevel.Item,
            Type = OfferType.SimpleDiscount,
            Example = "25% off all women's shoes",
            Description = "A straight discount on a set of items whenever they are part of the purchase. No pre-conditions, so the wizard goes directly to the rewards page.",
            ConditionKind = ConditionKind.None,
            RewardItemMode = RewardItemMode.DiscountedItems,
            AllowsFixedPrice = true
        },
        new()
        {
            Code = BuyXGetDiscount,
            Name = "Buy X, Get Discount",
            Level = OfferLevel.Item,
            Type = OfferType.BuyGet,
            Example = "Buy any 3 board games, get $10 off",
            Description = "The customer buys a quantity of qualifying items and the discount lands on those same items.",
            ConditionKind = ConditionKind.BuyQuantity,
            HasConditionItems = true,
            ConditionPriceRestriction = true,
            AllowsFixedPrice = true
        },
        new()
        {
            Code = SpendXGetDiscount,
            Name = "Spend X, Get Discount",
            Level = OfferLevel.Item,
            Type = OfferType.BuyGet,
            Example = "Spend $25 in Toys, get $5 off",
            Description = "The customer spends a threshold amount on qualifying items and the discount lands on those items.",
            ConditionKind = ConditionKind.SpendAmount,
            HasConditionItems = true,
            AllowsFixedPrice = true,
            AllowsApplyDiscountUpTo = false
        },
        new()
        {
            Code = BuyXGetYForDiscount,
            Name = "Buy X, Get Y for Discount",
            Level = OfferLevel.Item,
            Type = OfferType.BuyGet,
            Example = "Buy 2 pairs of shoes, get a pair of socks for 50% off",
            Description = "The customer buys a quantity of one set of items and receives a discount on a different set.",
            ConditionKind = ConditionKind.BuyQuantity,
            HasConditionItems = true,
            ConditionPriceRestriction = true,
            RewardItemMode = RewardItemMode.DiscountedItems,
            RewardPriceRestriction = true,
            AllowsFixedPrice = true,
            AllowsDistributionRule = true
        },
        new()
        {
            Code = SpendXGetYForDiscount,
            Name = "Spend X, Get Y for Discount",
            Level = OfferLevel.Item,
            Type = OfferType.BuyGet,
            Example = "Spend $15 on breakfast cereal, get 25% off any 2 cartons of milk",
            Description = "The customer spends a threshold amount on one set of items and receives a discount on a different set.",
            ConditionKind = ConditionKind.SpendAmount,
            HasConditionItems = true,
            RewardItemMode = RewardItemMode.DiscountedItems,
            RewardPriceRestriction = true,
            AllowsFixedPrice = true,
            AllowsDistributionRule = true
        },
        new()
        {
            Code = BuyXOfSingleItemForDiscount,
            Name = "Buy X of Single Item for Discount",
            Level = OfferLevel.Item,
            Type = OfferType.BuyGet,
            Example = "Buy 2 watermelons for $6",
            Description = "A quantity based discount on one specific item, chosen on the conditions page.",
            ConditionKind = ConditionKind.BuyQuantity,
            ConditionSingleItem = true,
            AllowsFixedPrice = true,
            AllowsApplyTo = false
        },
        new()
        {
            Code = BuyXAndYGetDiscount,
            Name = "Buy X and Y, Get Discount",
            Level = OfferLevel.Item,
            Type = OfferType.BuyGet,
            Example = "Buy a sandwich, chips, and drink for $5.00",
            Description = "Two or more buy conditions must all be met; the discount lands on the purchased items.",
            ConditionKind = ConditionKind.BuyQuantity,
            AllowsMultipleConditions = true,
            HasConditionItems = true,
            ConditionPriceRestriction = true,
            AllowsFixedPrice = true
        },
        new()
        {
            Code = BuyXAndYGetZForDiscount,
            Name = "Buy X and Y, Get Z for Discount",
            Level = OfferLevel.Item,
            Type = OfferType.BuyGet,
            Example = "Buy a scarf and hat, get 50% off gloves",
            Description = "Two or more buy conditions must all be met; the discount lands on a separate set of reward items.",
            ConditionKind = ConditionKind.BuyQuantity,
            AllowsMultipleConditions = true,
            HasConditionItems = true,
            ConditionPriceRestriction = true,
            RewardItemMode = RewardItemMode.DiscountedItems,
            RewardPriceRestriction = true,
            AllowsFixedPrice = true,
            AllowsDistributionRule = true
        },
        new()
        {
            Code = BuyXGetGiftWithPurchase,
            Name = "Buy X, Get Gift with Purchase",
            Level = OfferLevel.Item,
            Type = OfferType.GiftWithPurchase,
            Example = "Buy any 2 BBQ items, get a free beach towel",
            Description = "Buying a quantity of qualifying items earns a free item.",
            ConditionKind = ConditionKind.BuyQuantity,
            HasConditionItems = true,
            ConditionPriceRestriction = true,
            RewardKind = RewardKind.GiftItem,
            AllowsApplyTo = false,
            AllowsDistributionRule = true
        },
        new()
        {
            Code = SpendXGetGiftWithPurchase,
            Name = "Spend X, Get Gift with Purchase",
            Level = OfferLevel.Item,
            Type = OfferType.GiftWithPurchase,
            Example = "Spend $200 in racquets, get a free can of tennis balls",
            Description = "Spending a threshold amount on qualifying items earns a free item.",
            ConditionKind = ConditionKind.SpendAmount,
            HasConditionItems = true,
            RewardKind = RewardKind.GiftItem,
            AllowsApplyTo = false,
            AllowsDistributionRule = true
        },
        new()
        {
            Code = TransactionGetDiscount,
            Name = "Get Discount",
            Level = OfferLevel.Transaction,
            Type = OfferType.SimpleDiscount,
            Example = "10% off your purchase today only",
            Description = "A straight discount off the whole transaction, applied to every item unless excluded.",
            ConditionKind = ConditionKind.None,
            RewardItemMode = RewardItemMode.ExcludedItems,
            AllowsApplyDiscountUpTo = false
        },
        new()
        {
            Code = TransactionBuyXGetDiscount,
            Name = "Buy X, Get Discount",
            Level = OfferLevel.Transaction,
            Type = OfferType.BuyGet,
            Example = "Buy 3 reams of paper, get $5 off your purchase",
            Description = "Buying a quantity of qualifying items discounts the whole transaction.",
            ConditionKind = ConditionKind.BuyQuantity,
            HasConditionItems = true,
            ConditionPriceRestriction = true,
            RewardItemMode = RewardItemMode.ExcludedItems,
            AllowsApplyDiscountUpTo = false
        },
        new()
        {
            Code = TransactionSpendXGetDiscount,
            Name = "Spend X, Get Discount",
            Level = OfferLevel.Transaction,
            Type = OfferType.BuyGet,
            Example = "Spend $100 in cleaning supplies, get 5% off your purchase",
            Description = "Spending a threshold amount on qualifying items discounts the whole transaction.",
            ConditionKind = ConditionKind.SpendAmount,
            HasConditionItems = true,
            RewardItemMode = RewardItemMode.ExcludedItems,
            AllowsApplyDiscountUpTo = false
        }
    ];

    public static OfferTemplate? Find(string code) =>
        All.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<OfferTemplate> For(OfferLevel level, OfferType type) =>
        All.Where(t => t.Level == level && t.Type == type);
}
