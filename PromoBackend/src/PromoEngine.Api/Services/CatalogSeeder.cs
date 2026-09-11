using Microsoft.EntityFrameworkCore;
using PromoEngine.Domain.Catalog;
using PromoEngine.Domain.Templates;
using PromoEngine.Infrastructure.Catalog;

namespace PromoEngine.Api.Services;

/// <summary>
/// Creates the catalog database and the subscription plans that the sign up flow
/// offers. Plans differ by which offer templates they unlock, which is what makes
/// "based on the selected subscription, the user can access promotion creation"
/// concrete rather than cosmetic.
/// </summary>
public static class CatalogSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        await catalog.Database.EnsureCreatedAsync(ct);

        if (await catalog.Plans.AnyAsync(ct))
        {
            return;
        }

        var starterTemplates = string.Join(',',
            OfferTemplateCatalog.GetYForDiscount,
            OfferTemplateCatalog.BuyXGetDiscount,
            OfferTemplateCatalog.BuyXOfSingleItemForDiscount);

        var professionalTemplates = string.Join(',',
            OfferTemplateCatalog.GetYForDiscount,
            OfferTemplateCatalog.BuyXGetDiscount,
            OfferTemplateCatalog.SpendXGetDiscount,
            OfferTemplateCatalog.BuyXGetYForDiscount,
            OfferTemplateCatalog.SpendXGetYForDiscount,
            OfferTemplateCatalog.BuyXOfSingleItemForDiscount,
            OfferTemplateCatalog.BuyXAndYGetDiscount,
            OfferTemplateCatalog.BuyXAndYGetZForDiscount,
            OfferTemplateCatalog.TransactionGetDiscount,
            OfferTemplateCatalog.TransactionBuyXGetDiscount,
            OfferTemplateCatalog.TransactionSpendXGetDiscount);

        catalog.Plans.AddRange(
            new SubscriptionPlan
            {
                Code = "STARTER",
                Name = "Starter",
                Tagline = "Simple item discounts for a single banner",
                Description = "Everything needed to run straightforward percent, amount and fixed price offers on your own merchandise hierarchy.",
                MonthlyPrice = 149m,
                MaxPromotions = 25,
                MaxOffersPerPromotion = 5,
                MaxUsers = 3,
                AllowedTemplateCodes = starterTemplates,
                AllowsTransactionLevelOffers = false,
                AllowsGiftWithPurchase = false,
                AllowsEmergencyOffers = false,
                AllowsSpreadsheetUpload = false,
                DisplayOrder = 1
            },
            new SubscriptionPlan
            {
                Code = "PROFESSIONAL",
                Name = "Professional",
                Tagline = "Buy/get, spend thresholds and transaction offers",
                Description = "Adds spend thresholds, get-Y rewards, multi-condition buy/get offers and transaction level discounts, plus spreadsheet upload for bulk maintenance.",
                MonthlyPrice = 499m,
                MaxPromotions = 500,
                MaxOffersPerPromotion = 25,
                MaxUsers = 25,
                AllowedTemplateCodes = professionalTemplates,
                AllowsTransactionLevelOffers = true,
                AllowsGiftWithPurchase = false,
                AllowsEmergencyOffers = true,
                AllowsSpreadsheetUpload = true,
                IsHighlighted = true,
                DisplayOrder = 2
            },
            new SubscriptionPlan
            {
                Code = "ENTERPRISE",
                Name = "Enterprise",
                Tagline = "Every template, unlimited scale",
                Description = "All thirteen offer templates including gift with purchase, unlimited promotions and users, emergency price events and spreadsheet loading.",
                MonthlyPrice = 1499m,
                MaxPromotions = int.MaxValue,
                MaxOffersPerPromotion = 99,
                MaxUsers = int.MaxValue,
                AllowedTemplateCodes = "*",
                AllowsTransactionLevelOffers = true,
                AllowsGiftWithPurchase = true,
                AllowsEmergencyOffers = true,
                AllowsSpreadsheetUpload = true,
                DisplayOrder = 3
            });

        await catalog.SaveChangesAsync(ct);
    }
}
