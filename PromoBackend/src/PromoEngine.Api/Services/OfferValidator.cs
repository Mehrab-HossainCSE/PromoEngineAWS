using Microsoft.Extensions.Options;
using PromoEngine.Api.Contracts;
using PromoEngine.Domain.Catalog;
using PromoEngine.Domain.Promotions;
using PromoEngine.Domain.Templates;

namespace PromoEngine.Api.Services;

public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>
    /// Number of days required between today and the effective date of a price event,
    /// so stores can react. Emergency offers bypass this rule.
    /// </summary>
    public int PriceEventProcessingDays { get; set; } = 2;

    /// <summary>When false the unit of measure field is hidden and never validated.</summary>
    public bool MultipleUnitsOfMeasure { get; set; } = true;
}

public sealed record ValidationOutcome(bool IsValid, Dictionary<string, string[]> Errors)
{
    public static ValidationOutcome Valid() => new(true, []);
}

/// <summary>
/// Enforces the template rules from the promotions guide plus the subscription limits
/// of the caller's plan, so an offer can never be saved in a shape the template does
/// not support.
/// </summary>
public sealed class OfferValidator(IOptions<PricingOptions> pricingOptions)
{
    private readonly PricingOptions _pricing = pricingOptions.Value;

    public ValidationOutcome Validate(SaveOfferRequest request, SubscriptionPlan plan)
    {
        var errors = new Dictionary<string, List<string>>();

        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                list = [];
                errors[field] = list;
            }

            list.Add(message);
        }

        var template = OfferTemplateCatalog.Find(request.TemplateCode);
        if (template is null)
        {
            Add(nameof(request.TemplateCode), $"Unknown offer template '{request.TemplateCode}'.");
            return Finish(errors);
        }

        // ---- plan entitlements -------------------------------------------------
        if (!plan.AllowsTemplate(template.Code))
        {
            Add(nameof(request.TemplateCode), $"The {plan.Name} plan does not include the {template.Name} template.");
        }

        if (template.Level == OfferLevel.Transaction && !plan.AllowsTransactionLevelOffers)
        {
            Add(nameof(request.TemplateCode), $"The {plan.Name} plan does not include transaction level offers.");
        }

        if (template.Type == OfferType.GiftWithPurchase && !plan.AllowsGiftWithPurchase)
        {
            Add(nameof(request.TemplateCode), $"The {plan.Name} plan does not include gift with purchase offers.");
        }

        if (request.IsEmergency && !plan.AllowsEmergencyOffers)
        {
            Add(nameof(request.IsEmergency), $"The {plan.Name} plan does not include emergency price events.");
        }

        // ---- dates -------------------------------------------------------------
        if (request.EndDate is not null && request.EndDate < request.StartDate)
        {
            Add(nameof(request.EndDate), "End date cannot be before the start date.");
        }

        if (!request.IsEmergency)
        {
            var earliest = DateTimeOffset.UtcNow.Date.AddDays(_pricing.PriceEventProcessingDays);
            if (request.StartDate.Date < earliest)
            {
                Add(nameof(request.StartDate),
                    $"Start date must be at least {_pricing.PriceEventProcessingDays} day(s) out ({earliest:yyyy-MM-dd}). Create an emergency offer to bypass this rule.");
            }
        }

        // ---- conditions --------------------------------------------------------
        if (template.HasConditions)
        {
            if (request.Conditions.Count == 0)
            {
                Add(nameof(request.Conditions), $"The {template.Name} template requires at least one condition.");
            }

            if (template.AllowsMultipleConditions && request.Conditions.Count < 2)
            {
                Add(nameof(request.Conditions), $"The {template.Name} template requires at least two buy conditions.");
            }

            if (!template.AllowsMultipleConditions && request.Conditions.Count > 1)
            {
                Add(nameof(request.Conditions), $"The {template.Name} template supports a single condition.");
            }

            for (var i = 0; i < request.Conditions.Count; i++)
            {
                var condition = request.Conditions[i];
                var prefix = $"Conditions[{i}]";

                if (template.ConditionKind == ConditionKind.BuyQuantity)
                {
                    if (condition.BuyQuantity is null or <= 0)
                    {
                        Add($"{prefix}.BuyQuantity", "Buy quantity must be greater than zero.");
                    }

                    if (_pricing.MultipleUnitsOfMeasure && string.IsNullOrWhiteSpace(condition.UnitOfMeasure))
                    {
                        Add($"{prefix}.UnitOfMeasure", "Unit of measure is required.");
                    }
                }

                if (template.ConditionKind == ConditionKind.SpendAmount && condition.SpendAmount is null or <= 0)
                {
                    Add($"{prefix}.SpendAmount", "Spend amount must be greater than zero.");
                }

                var restriction = Mapping.ParseEnum(condition.PriceRestriction, PriceRestrictionOperator.None);
                if (restriction != PriceRestrictionOperator.None)
                {
                    if (!template.ConditionPriceRestriction)
                    {
                        Add($"{prefix}.PriceRestriction", $"The {template.Name} template does not support a price restriction on conditions.");
                    }

                    if (condition.PriceRestrictionFrom is null)
                    {
                        Add($"{prefix}.PriceRestrictionFrom", "Enter the price the restriction is measured against.");
                    }

                    if (restriction == PriceRestrictionOperator.Between)
                    {
                        if (condition.PriceRestrictionTo is null)
                        {
                            Add($"{prefix}.PriceRestrictionTo", "Enter the upper bound of the price range.");
                        }
                        else if (condition.PriceRestrictionFrom is not null && condition.PriceRestrictionTo <= condition.PriceRestrictionFrom)
                        {
                            Add($"{prefix}.PriceRestrictionTo", "The upper bound must be greater than the lower bound.");
                        }
                    }
                }

                if (template.HasConditionItems && !condition.Items.Any(IsInclude))
                {
                    Add($"{prefix}.Items", "Add at least one included item to the qualifying items.");
                }
            }
        }
        else if (request.Conditions.Count > 0)
        {
            Add(nameof(request.Conditions), $"The {template.Name} template does not take conditions.");
        }

        // ---- reward ------------------------------------------------------------
        var reward = request.Reward;
        if (reward is null)
        {
            Add(nameof(request.Reward), "Reward details are required.");
            return Finish(errors);
        }

        if (template.RewardKind == RewardKind.GiftItem)
        {
            if (string.IsNullOrWhiteSpace(reward.GiftItemId))
            {
                Add("Reward.GiftItemId", "Select the free item granted by this offer.");
            }
        }
        else
        {
            var discountType = Mapping.ParseEnum(reward.DiscountType, DiscountType.PercentOff);

            if (discountType == DiscountType.FreeItem)
            {
                Add("Reward.DiscountType", $"The {template.Name} template rewards a discount, not a free item.");
            }

            if (discountType == DiscountType.FixedPrice && !template.AllowsFixedPrice)
            {
                Add("Reward.DiscountType", $"The {template.Name} template does not support a fixed price reward.");
            }

            if (reward.DiscountValue is null or <= 0)
            {
                Add("Reward.DiscountValue", "Enter a discount greater than zero.");
            }
            else if (discountType == DiscountType.PercentOff && reward.DiscountValue > 100)
            {
                Add("Reward.DiscountValue", "A percentage discount cannot exceed 100.");
            }

            if (discountType != DiscountType.PercentOff
                && !reward.AppliesToAllCurrencies
                && string.IsNullOrWhiteSpace(reward.CurrencyCode))
            {
                Add("Reward.CurrencyCode", "Select the currency the reward applies to, or apply it to all currencies.");
            }
        }

        if (template.ConditionSingleItem && string.IsNullOrWhiteSpace(reward.SingleItemId))
        {
            Add("Reward.SingleItemId", "Select the item this offer discounts.");
        }

        if (!template.AllowsApplyDiscountUpTo && reward.ApplyDiscountUpTo is not null)
        {
            Add("Reward.ApplyDiscountUpTo", $"The {template.Name} template does not support an application limit.");
        }

        if (reward.ApplyDiscountUpTo is <= 0)
        {
            Add("Reward.ApplyDiscountUpTo", "The application limit must be greater than zero, or left blank for unlimited.");
        }

        // ---- reward items ------------------------------------------------------
        switch (template.RewardItemMode)
        {
            case RewardItemMode.DiscountedItems when !request.RewardItems.Any(IsInclude):
                Add(nameof(request.RewardItems), "Add at least one included item that the reward applies to.");
                break;

            case RewardItemMode.None when request.RewardItems.Count > 0:
                Add(nameof(request.RewardItems), $"The {template.Name} template does not take an item list on the rewards page.");
                break;
        }

        // ---- distribution rule -------------------------------------------------
        var distribution = Mapping.ParseEnum(request.DistributionRule, DistributionRule.NotApplicable);
        if (distribution != DistributionRule.NotApplicable && !template.AllowsDistributionRule)
        {
            Add(nameof(request.DistributionRule), $"Distribution rules do not apply to the {template.Name} template.");
        }

        return Finish(errors);
    }

    private static bool IsInclude(SaveOfferItemRequest item) =>
        Mapping.ParseEnum(item.Action, SelectionAction.Include) == SelectionAction.Include;

    private static ValidationOutcome Finish(Dictionary<string, List<string>> errors) =>
        errors.Count == 0
            ? ValidationOutcome.Valid()
            : new ValidationOutcome(false, errors.ToDictionary(k => k.Key, v => v.Value.ToArray()));
}
