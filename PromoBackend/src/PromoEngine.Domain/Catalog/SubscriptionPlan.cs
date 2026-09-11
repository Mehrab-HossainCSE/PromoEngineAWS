namespace PromoEngine.Domain.Catalog;

/// <summary>
/// A product the customer subscribes to. The plan gates which offer templates and
/// which capabilities (locations, spreadsheet upload, emergency offers) a tenant may use.
/// </summary>
public class SubscriptionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable code used by the API and the UI, for example "STARTER".</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public decimal MonthlyPrice { get; set; }
    public string CurrencyCode { get; set; } = "USD";

    public int MaxPromotions { get; set; }
    public int MaxOffersPerPromotion { get; set; }
    public int MaxUsers { get; set; }

    /// <summary>Comma separated list of offer template codes the plan unlocks. "*" means every template.</summary>
    public string AllowedTemplateCodes { get; set; } = "*";

    public bool AllowsTransactionLevelOffers { get; set; }
    public bool AllowsGiftWithPurchase { get; set; }
    public bool AllowsEmergencyOffers { get; set; }
    public bool AllowsSpreadsheetUpload { get; set; }
    public bool AllowsDedicatedDatabase { get; set; } = true;

    /// <summary>Ordering hint for the pricing page.</summary>
    public int DisplayOrder { get; set; }

    public bool IsHighlighted { get; set; }
    public bool IsActive { get; set; } = true;

    public bool AllowsTemplate(string templateCode) =>
        AllowedTemplateCodes.Trim() == "*" ||
        AllowedTemplateCodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(templateCode, StringComparer.OrdinalIgnoreCase);
}
