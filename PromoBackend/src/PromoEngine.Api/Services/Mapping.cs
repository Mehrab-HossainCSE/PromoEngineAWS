using PromoEngine.Api.Contracts;
using PromoEngine.Domain.Catalog;
using PromoEngine.Domain.Promotions;
using PromoEngine.Domain.Templates;

namespace PromoEngine.Api.Services;

/// <summary>Domain to contract projections. Kept in one place so responses stay consistent.</summary>
public static class Mapping
{
    public static PlanResponse ToResponse(this SubscriptionPlan plan) => new()
    {
        Id = plan.Id,
        Code = plan.Code,
        Name = plan.Name,
        Tagline = plan.Tagline,
        Description = plan.Description,
        MonthlyPrice = plan.MonthlyPrice,
        CurrencyCode = plan.CurrencyCode,
        MaxPromotions = plan.MaxPromotions,
        MaxOffersPerPromotion = plan.MaxOffersPerPromotion,
        MaxUsers = plan.MaxUsers,
        AllowsTransactionLevelOffers = plan.AllowsTransactionLevelOffers,
        AllowsGiftWithPurchase = plan.AllowsGiftWithPurchase,
        AllowsEmergencyOffers = plan.AllowsEmergencyOffers,
        AllowsSpreadsheetUpload = plan.AllowsSpreadsheetUpload,
        IsHighlighted = plan.IsHighlighted,
        AllowedTemplateCodes = plan.AllowedTemplateCodes.Trim() == "*"
            ? []
            : plan.AllowedTemplateCodes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Features = BuildFeatures(plan)
    };

    private static List<string> BuildFeatures(SubscriptionPlan plan)
    {
        var templateCount = plan.AllowedTemplateCodes.Trim() == "*"
            ? OfferTemplateCatalog.All.Count
            : plan.AllowedTemplateCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

        var features = new List<string>
        {
            plan.MaxPromotions == int.MaxValue ? "Unlimited promotions" : $"Up to {plan.MaxPromotions:N0} promotions",
            $"{templateCount} offer templates",
            $"{plan.MaxOffersPerPromotion} offers per promotion",
            plan.MaxUsers == int.MaxValue ? "Unlimited users" : $"{plan.MaxUsers} users",
            "Dedicated tenant database"
        };

        if (plan.AllowsTransactionLevelOffers) features.Add("Transaction level offers");
        if (plan.AllowsGiftWithPurchase) features.Add("Gift with purchase");
        if (plan.AllowsEmergencyOffers) features.Add("Emergency price events");
        if (plan.AllowsSpreadsheetUpload) features.Add("Spreadsheet upload and download");

        return features;
    }

    public static SubscriptionResponse ToResponse(this TenantSubscription subscription) => new()
    {
        Id = subscription.Id,
        PlanCode = subscription.Plan?.Code ?? string.Empty,
        PlanName = subscription.Plan?.Name ?? string.Empty,
        Status = subscription.Status.ToString(),
        StartsAtUtc = subscription.StartsAtUtc,
        TrialEndsAtUtc = subscription.TrialEndsAtUtc,
        IsActive = subscription.IsCurrentlyActive
    };

    public static UserProfileResponse ToProfile(this TenantUser user, Tenant tenant, TenantSubscription? subscription) => new()
    {
        UserId = user.Id,
        Email = user.Email,
        DisplayName = user.DisplayName,
        Role = user.Role.ToString(),
        TenantId = tenant.Id,
        TenantSlug = tenant.Slug,
        CompanyName = tenant.CompanyName,
        ProvisioningStatus = tenant.ProvisioningStatus.ToString(),
        DatabaseName = tenant.DatabaseName,
        Subscription = subscription?.ToResponse()
    };

    public static OfferTemplateResponse ToResponse(this OfferTemplate template, bool availableOnPlan) => new()
    {
        Code = template.Code,
        Name = template.Name,
        Level = template.Level.ToString(),
        Type = template.Type.ToString(),
        Example = template.Example,
        Description = template.Description,
        ConditionKind = template.ConditionKind.ToString(),
        HasConditions = template.HasConditions,
        AllowsMultipleConditions = template.AllowsMultipleConditions,
        HasConditionItems = template.HasConditionItems,
        ConditionPriceRestriction = template.ConditionPriceRestriction,
        ConditionSingleItem = template.ConditionSingleItem,
        RewardKind = template.RewardKind.ToString(),
        RewardItemMode = template.RewardItemMode.ToString(),
        RewardPriceRestriction = template.RewardPriceRestriction,
        AllowsFixedPrice = template.AllowsFixedPrice,
        AllowsApplyTo = template.AllowsApplyTo,
        AllowsApplyDiscountUpTo = template.AllowsApplyDiscountUpTo,
        AllowsDistributionRule = template.AllowsDistributionRule,
        AvailableOnCurrentPlan = availableOnPlan
    };

    public static PromotionSummaryResponse ToSummary(this Promotion promotion) => new()
    {
        Id = promotion.Id,
        PromotionNumber = promotion.PromotionNumber,
        Description = promotion.Description,
        CampaignCode = promotion.Campaign?.Code,
        Status = promotion.Status.ToString(),
        OfferCount = promotion.Offers.Count,
        StartDate = promotion.StartDate,
        EndDate = promotion.EndDate,
        CreatedAtUtc = promotion.CreatedAtUtc
    };

    public static PromotionDetailResponse ToDetail(this Promotion promotion) => new()
    {
        Id = promotion.Id,
        PromotionNumber = promotion.PromotionNumber,
        Description = promotion.Description,
        CampaignId = promotion.CampaignId,
        CampaignCode = promotion.Campaign?.Code,
        Status = promotion.Status.ToString(),
        OfferCount = promotion.Offers.Count,
        StartDate = promotion.StartDate,
        EndDate = promotion.EndDate,
        CreatedAtUtc = promotion.CreatedAtUtc,
        Offers = promotion.Offers.OrderBy(o => o.OfferNumber).Select(o => o.ToResponse()).ToList()
    };

    public static OfferResponse ToResponse(this Offer offer) => new()
    {
        Id = offer.Id,
        PromotionId = offer.PromotionId,
        OfferNumber = offer.OfferNumber,
        Description = offer.Description,
        Level = offer.Level.ToString(),
        Type = offer.Type.ToString(),
        TemplateCode = offer.TemplateCode,
        TemplateName = OfferTemplateCatalog.Find(offer.TemplateCode)?.Name ?? offer.TemplateCode,
        StartDate = offer.StartDate,
        StartTime = offer.StartTime,
        EndDate = offer.EndDate,
        EndTime = offer.EndTime,
        Comments = offer.Comments,
        CouponCode = offer.CouponCode,
        CouponCodeRequired = offer.CouponCodeRequired,
        DistributionRule = offer.DistributionRule.ToString(),
        ExclusiveDiscount = offer.ExclusiveDiscount,
        CustomerDescription = offer.CustomerDescription,
        Status = offer.Status.ToString(),
        IsEmergency = offer.IsEmergency,
        CancelReason = offer.CancelReason,
        ApprovedBy = offer.ApprovedBy,
        ApprovedAtUtc = offer.ApprovedAtUtc,
        Conditions = offer.Conditions.OrderBy(c => c.Sequence).Select(c => c.ToResponse(offer)).ToList(),
        Reward = offer.Reward?.ToResponse(),
        RewardItems = offer.Items
            .Where(i => i.Scope == ItemScope.Reward)
            .Select(i => i.ToResponse())
            .ToList(),
        Locations = offer.Locations.Select(l => l.ToResponse()).ToList(),
        Products = offer.Products
            .OrderBy(p => p.SourceFileName)
            .ThenBy(p => p.SourceRowNumber)
            .Select(p => p.ToResponse())
            .ToList()
    };

    public static OfferProductResponse ToResponse(this OfferProduct product) => new()
    {
        Id = product.Id,
        Barcode = product.Barcode,
        ItemId = product.ItemId,
        StyleCode = product.StyleCode,
        ItemDescription = product.ItemDescription,
        Department = product.Department,
        Class = product.Class,
        Subclass = product.Subclass,
        Brand = product.Brand,
        VendorName = product.VendorName,
        SupplierSite = product.SupplierSite,
        Action = product.Action.ToString(),
        GetApplicablePromotions = product.GetApplicablePromotions,
        PromoTypeId = product.PromoTypeId,
        SiteCode = product.SiteCode,
        CustomerTier = product.CustomerTier,
        SourceFileName = product.SourceFileName,
        SourceRowNumber = product.SourceRowNumber,
        UploadedBy = product.UploadedBy,
        UploadedAtUtc = product.UploadedAtUtc
    };

    public static ConditionResponse ToResponse(this OfferCondition condition, Offer offer) => new()
    {
        Id = condition.Id,
        Sequence = condition.Sequence,
        BuyQuantity = condition.BuyQuantity,
        UnitOfMeasure = condition.UnitOfMeasure,
        SpendAmount = condition.SpendAmount,
        CurrencyCode = condition.CurrencyCode,
        PriceRestriction = condition.PriceRestriction.ToString(),
        PriceRestrictionFrom = condition.PriceRestrictionFrom,
        PriceRestrictionTo = condition.PriceRestrictionTo,
        PriceRestrictionCurrency = condition.PriceRestrictionCurrency,
        Items = (condition.Items.Count > 0
                ? condition.Items
                : offer.Items.Where(i => i.ConditionId == condition.Id))
            .Select(i => i.ToResponse())
            .ToList()
    };

    public static RewardResponse ToResponse(this OfferReward reward) => new()
    {
        DiscountType = reward.DiscountType.ToString(),
        DiscountValue = reward.DiscountValue,
        CurrencyCode = reward.CurrencyCode,
        AppliesToAllCurrencies = reward.AppliesToAllCurrencies,
        ApplyTo = reward.ApplyTo.ToString(),
        ApplyDiscountUpTo = reward.ApplyDiscountUpTo,
        GetQuantity = reward.GetQuantity,
        GiftItemId = reward.GiftItemId,
        GiftItemDescription = reward.GiftItemDescription,
        SingleItemId = reward.SingleItemId,
        SingleItemDescription = reward.SingleItemDescription
    };

    public static OfferItemResponse ToResponse(this OfferItem item) => new()
    {
        Id = item.Id,
        Scope = item.Scope.ToString(),
        Action = item.Action.ToString(),
        Level = item.Level.ToString(),
        Summary = item.Summary,
        Department = item.Department,
        Class = item.Class,
        Subclass = item.Subclass,
        ItemId = item.ItemId,
        ItemDescription = item.ItemDescription,
        Barcode = item.Barcode,
        StyleCode = item.StyleCode,
        VendorName = item.VendorName,
        SourceRowNumber = item.SourceRowNumber,
        ParentItemId = item.ParentItemId,
        DiffType = item.DiffType,
        DiffValue = item.DiffValue,
        ItemListId = item.ItemListId,
        SourceFileName = item.SourceFileName,
        SupplierSite = item.SupplierSite,
        Brand = item.Brand,
        CancelReason = item.CancelReason,
        CancelledAtUtc = item.CancelledAtUtc
    };

    public static OfferLocationResponse ToResponse(this OfferLocation location) => new()
    {
        Id = location.Id,
        Action = location.Action.ToString(),
        Level = location.Level.ToString(),
        Summary = location.Summary,
        ZoneGroup = location.ZoneGroup,
        Zone = location.Zone,
        LocationList = location.LocationList,
        Store = location.Store,
        StoreName = location.StoreName,
        CancelReason = location.CancelReason,
        CancelledAtUtc = location.CancelledAtUtc
    };

    public static CampaignResponse ToResponse(this Campaign campaign) => new()
    {
        Id = campaign.Id,
        Code = campaign.Code,
        Description = campaign.Description
    };

    /// <summary>Parses an enum name, falling back to the supplied default for null or unknown values.</summary>
    public static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}

