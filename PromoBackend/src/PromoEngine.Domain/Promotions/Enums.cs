namespace PromoEngine.Domain.Promotions;

/// <summary>Offer level, as defined in the Pricing promotions guide.</summary>
public enum OfferLevel
{
    Item = 0,
    Transaction = 1
}

public enum OfferType
{
    SimpleDiscount = 0,
    BuyGet = 1,
    GiftWithPurchase = 2
}

/// <summary>
/// Status flow of a promotion or offer: Worksheet -> Submitted -> Approved -> Active -> Completed.
/// Rejected and Cancelled are terminal side states.
/// </summary>
public enum PromotionStatus
{
    Worksheet = 0,
    Submitted = 1,
    Approved = 2,
    Rejected = 3,
    Active = 4,
    Completed = 5,
    Cancelled = 6
}

/// <summary>How the discount is spread across the items of a buy/get offer.</summary>
public enum DistributionRule
{
    NotApplicable = 0,
    BuyItems = 1,
    GetItems = 2,
    BothBuyAndGetItems = 3
}

public enum DiscountType
{
    PercentOff = 0,
    AmountOff = 1,
    FixedPrice = 2,
    /// <summary>Used by gift with purchase templates - the reward is an item, not a discount.</summary>
    FreeItem = 3
}

/// <summary>Which retail price the reward applies to.</summary>
public enum ApplyToPriceType
{
    Regular = 0,
    Clearance = 1,
    RegularAndClearance = 2
}

/// <summary>Optional price band filter applied to the qualifying items of a condition.</summary>
public enum PriceRestrictionOperator
{
    None = 0,
    GreaterThan = 1,
    LessThan = 2,
    Between = 3
}

/// <summary>Merchandise hierarchy level used when including or excluding items.</summary>
public enum ItemSelectionLevel
{
    AllDepartments = 0,
    Department = 1,
    Class = 2,
    Subclass = 3,
    Item = 4,
    ParentDiff = 5,
    ItemList = 6,

    /// <summary>
    /// Legacy. Item rules used to be creatable from a spreadsheet; that upload is now
    /// the offer's product sheet instead. Kept so rules stored under the old flow
    /// still read back, but nothing creates one.
    /// </summary>
    UploadList = 7,
    SupplierSiteBrand = 8
}

public enum SelectionAction
{
    Include = 0,
    Exclude = 1
}

/// <summary>Whether an item row qualifies the customer (buy) or receives the reward (get).</summary>
public enum ItemScope
{
    Condition = 0,
    Reward = 1
}

public enum LocationLevel
{
    Zone = 0,
    LocationList = 1,
    Store = 2
}
