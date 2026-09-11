using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PromoEngine.Api.Contracts;
using PromoEngine.Domain.Catalog;
using PromoEngine.Domain.Promotions;
using PromoEngine.Infrastructure.Catalog;
using PromoEngine.Infrastructure.Tenancy;

namespace PromoEngine.Api.Services;

/// <summary>Settings for the POS-facing applicable-promotions endpoint.</summary>
public sealed class ExternalPromotionApiOptions
{
    public const string SectionName = "ExternalPromotionApi";

    /// <summary>Shared key supplied as HEADER-API-KEY by the consuming system.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Pins the integration to one tenant. Set, it is the only tenant this endpoint
    /// will answer from and a request naming any other is refused - which is what a
    /// single-customer install wants, because the shared API key then cannot be used
    /// to read another customer's promotions. Left blank, the request's TenantId
    /// chooses the database.
    /// </summary>
    public string TenantSlug { get; init; } = string.Empty;
}

/// <summary>
/// Read-only evaluator for the external "get applicable promotions" contract.
/// It deliberately does not mutate offers or their status.
///
/// The request names its tenant, which is resolved to that customer's isolated
/// database, and everything below is read from there. Two sources are consulted,
/// in this order:
///
/// 1. <see cref="OfferProduct"/> rows uploaded against an offer in the wizard. This
///    is the primary source: a POS sends a barcode, and those rows say which offer
///    that barcode belongs to. The discount always comes from the offer's reward.
/// 2. The offers themselves, for offers with no uploaded sheet, so an offer built
///    only from hierarchy rules still answers the till.
///
/// A basket that matches neither gets an empty array - the document defines no other
/// shape for "nothing applies", so 200 with [] is how no discount is reported.
/// </summary>
public sealed class ApplicablePromotionService(
    CatalogDbContext catalog,
    ITenantDbContextFactory contextFactory,
    IOptions<ExternalPromotionApiOptions> options,
    ILogger<ApplicablePromotionService> logger)
{
    /// <summary>
    /// Answers the basket, or reports why the caller's tenant could not be found.
    /// The failure is returned rather than thrown because it is the caller's mistake
    /// to fix, and the integration document describes it as a 400 with a message.
    /// </summary>
    public async Task<(IReadOnlyList<ApplicablePromotionResponse>? Results, string? Error)> GetAsync(
        GetApplicablePromotionsRequest request, CancellationToken ct)
    {
        var (tenant, error) = await ResolveTenantAsync(request.TenantId, ct);
        if (tenant is null) return (null, error);

        logger.LogDebug(
            "Answering get-applicable-promotions for tenant {Slug} at site {Site}",
            tenant.Slug, request.SiteCode);

        return (await EvaluateTenantAsync(tenant.ConnectionString!, request, ct), null);
    }

    /// <summary>
    /// Finds the one tenant database the request may be answered from.
    ///
    /// The identifier is matched against the tenant id, the slug and the sign-in
    /// email, all three of which are unique, so an integrator can pass whichever one
    /// they were handed without a fourth identifier scheme being invented for them.
    /// </summary>
    private async Task<TenantResolution> ResolveTenantAsync(string? identifier, CancellationToken ct)
    {
        var pinned = options.Value.TenantSlug.Trim();
        var wanted = identifier?.Trim() ?? string.Empty;

        if (wanted.Length == 0)
        {
            return new TenantResolution(null,
                "TenantId is required. Send the tenant id, tenant slug or account email of the "
                + "customer whose promotions you are asking about.");
        }

        var tenants = await catalog.Tenants.AsNoTracking().ToListAsync(ct);

        var tenant = tenants.FirstOrDefault(t =>
            string.Equals(t.Id.ToString(), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Slug, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Email, wanted, StringComparison.OrdinalIgnoreCase));

        if (tenant is null)
        {
            // The identifier is echoed back but nothing is said about which tenants do
            // exist, so a wrong value cannot be used to enumerate customers.
            logger.LogWarning("get-applicable-promotions called with unknown TenantId {TenantId}", wanted);
            return new TenantResolution(null, $"Unknown TenantId '{wanted}'.");
        }

        // A pinned deployment is also a whitelist: the shared API key must not be
        // usable to read a tenant this integration was never meant to see.
        if (pinned.Length > 0 && !string.Equals(tenant.Slug, pinned, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "get-applicable-promotions asked for tenant {Wanted} but this deployment is pinned to {Pinned}",
                tenant.Slug, pinned);

            return new TenantResolution(null, $"This integration is not authorised for TenantId '{wanted}'.");
        }

        if (tenant.ProvisioningStatus != ProvisioningStatus.Ready || tenant.ConnectionString is null)
        {
            return new TenantResolution(null,
                $"Tenant '{wanted}' is not ready yet. Its database is still being provisioned.");
        }

        return new TenantResolution(tenant, null);
    }

    /// <summary>Why a request could not be pointed at a tenant database.</summary>
    private sealed record TenantResolution(Tenant? Tenant, string? Error);

    private async Task<List<ApplicablePromotionResponse>> EvaluateTenantAsync(
        string connectionString, GetApplicablePromotionsRequest request, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForConnectionString(connectionString);

        var now = DateTimeOffset.UtcNow;
        var barcodes = request.Items
            .Select(i => i.Barcode)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (barcodes.Count == 0) return [];

        var responses = new List<ApplicablePromotionResponse>();

        // An offer that carries an uploaded sheet is answered from that sheet alone.
        // The sheet is the retailer's statement of which barcodes take part, so falling
        // back to the offer's hierarchy rules would serve items it deliberately leaves out.
        var publishedOfferIds = await db.OfferProducts.AsNoTracking()
            .Select(p => p.OfferId)
            .Distinct()
            .ToListAsync(ct);

        // ---- 1. Uploaded offer product rows ---------------------------------
        var uploaded = await db.OfferProducts.AsNoTracking()
            .Include(p => p.Offer).ThenInclude(o => o!.Promotion)
            .Include(p => p.Offer).ThenInclude(o => o!.Reward)
            .Include(p => p.Offer).ThenInclude(o => o!.Conditions)
            .Include(p => p.Offer).ThenInclude(o => o!.Locations)
            .Where(p => barcodes.Contains(p.Barcode))
            .ToListAsync(ct);

        foreach (var group in uploaded.GroupBy(p => p.OfferId))
        {
            var offer = group.First().Offer;
            if (offer?.Promotion is null || !IsServable(offer, now)) continue;
            if (!IsLive(offer.Promotion.Status)) continue;
            if (!IsAtSite(offer, request.SiteCode)) continue;

            // An offer with no stated discount has nothing to tell the till, and
            // saying "0.00%" would look like a working answer while applying nothing.
            if (DescribeReward(offer.Reward) is not { } offerText) continue;

            // Exclude rows carve a barcode back out of the sheet, so they are removed
            // before anything is measured rather than after.
            var excluded = group
                .Where(row => row.Action == SelectionAction.Exclude)
                .Select(row => row.Barcode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var covered = group
                .Where(row => row.Action == SelectionAction.Include)
                .Where(row => row.GetApplicablePromotions)
                .Where(row => MatchesScope(row, request))
                .Select(row => row.Barcode)
                .Where(barcode => !excluded.Contains(barcode))
                .ToList();

            if (covered.Count == 0) continue;

            // The offer's buy/spend conditions are measured against the covered
            // barcodes the basket actually carries, so "buy 3" means three of the
            // items this offer covers - the till has no merchandise hierarchy to
            // evaluate the offer's own item rules with.
            if (!MeetsOfferConditions(offer, Aggregate(covered, request.Items))) continue;

            var promotion = offer.Promotion;

            responses.Add(new ApplicablePromotionResponse
            {
                PromoId = promotion.PromotionNumber,
                PromoNo = $"[{promotion.PromotionNumber}] {promotion.Description}",
                SlabId = offer.OfferNumber,
                SlabNo = offer.OfferNumber.ToString(),
                SlabDesc = offer.Description,
                IsCrossCategory = 0,
                PromoOffers = [new ApplicablePromotionOfferResponse
                {
                    OfferId = offer.OfferNumber,
                    Offer = offerText
                }]
            });
        }

        // ---- 2. Offers with no uploaded sheet -------------------------------
        var offers = await db.Offers.AsNoTracking()
            .Include(o => o.Promotion)
            .Include(o => o.Conditions).ThenInclude(c => c.Items)
            .Include(o => o.Reward)
            .Include(o => o.Items)
            .Include(o => o.Locations)
            .Where(o => o.Status == PromotionStatus.Active
                && o.StartDate <= now
                && (o.EndDate == null || o.EndDate >= now))
            .ToListAsync(ct);

        responses.AddRange(offers
            .Where(o => !publishedOfferIds.Contains(o.Id))
            .Where(o => IsAtSite(o, request.SiteCode))
            .Where(o => MeetsConditions(o, request.Items))
            .OrderBy(o => o.Promotion!.PromotionNumber)
            .ThenBy(o => o.OfferNumber)
            .Select(ToResponse)
            .OfType<ApplicablePromotionResponse>());

        return responses;
    }

    // -----------------------------------------------------------------------
    // Uploaded product matching
    // -----------------------------------------------------------------------

    /// <summary>A promotion the till should be told about. Drafts and dead ones are not.</summary>
    private static bool IsLive(PromotionStatus status) =>
        status is not (PromotionStatus.Cancelled or PromotionStatus.Rejected or PromotionStatus.Completed);

    /// <summary>
    /// Whether an offer may supply terms to a till. Workflow status is deliberately not
    /// gated beyond the dead states - the uploaded sheet is the publishing decision, and
    /// this integration is documented as not waiting on the approval flow. The dates are
    /// gated, because an offer that has not started must not discount anything yet.
    /// </summary>
    private static bool IsServable(Offer offer, DateTimeOffset now) =>
        offer.Status is not (PromotionStatus.Cancelled or PromotionStatus.Rejected)
        && offer.StartDate <= now
        && (offer.EndDate is null || offer.EndDate >= now);

    /// <summary>
    /// The offer's conditions measured against the covered barcodes in the basket, so
    /// "buy 3" means three of the items this offer actually covers.
    /// </summary>
    private static bool MeetsOfferConditions(Offer offer, BasketLine covered) =>
        offer.Conditions.Count == 0
        || offer.Conditions.All(condition =>
            (!condition.BuyQuantity.HasValue || covered.Quantity >= condition.BuyQuantity.Value)
            && (!condition.SpendAmount.HasValue || covered.Value >= condition.SpendAmount.Value));

    /// <summary>Total quantity and value the basket carries across the given barcodes.</summary>
    private static BasketLine Aggregate(
        IEnumerable<string> barcodes, IReadOnlyList<ApplicablePromotionItemRequest> items)
    {
        var set = barcodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = items.Where(i => set.Contains(i.Barcode)).ToList();

        return new BasketLine(lines.Sum(i => i.InvoiceQty), lines.Sum(i => i.UnitPrice * i.InvoiceQty));
    }

    /// <summary>
    /// Site, promotion type and customer tier all follow the same rule: a blank cell
    /// on the sheet means "any", a filled one has to match what the POS sent.
    /// </summary>
    private static bool MatchesScope(OfferProduct row, GetApplicablePromotionsRequest request) =>
        ScopeMatches(row.SiteCode, request.SiteCode)
        && ScopeMatches(row.PromoTypeId, request.PromotypeID)
        && ScopeMatches(row.CustomerTier, request.CustomerTier);

    private static bool ScopeMatches(string? sheetValue, string? requestValue) =>
        string.IsNullOrWhiteSpace(sheetValue)
        || string.Equals(sheetValue.Trim(), requestValue?.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed record BasketLine(int Quantity, decimal Value);

    // -----------------------------------------------------------------------
    // Offer matching (offers with no uploaded sheet)
    // -----------------------------------------------------------------------

    private static bool IsAtSite(Offer offer, string siteCode)
    {
        var rules = offer.Locations.Where(l => l.CancelledAtUtc is null).ToList();
        if (rules.Count == 0) return true;

        // A POS site code maps directly to Store rules. Zone and location-list
        // expansion belongs to the retailer's location master and is intentionally
        // not guessed here.
        var directRules = rules.Where(l => l.Level == LocationLevel.Store
            && string.Equals(l.Store, siteCode, StringComparison.OrdinalIgnoreCase)).ToList();
        var hasIncludes = rules.Any(l => l.Action == SelectionAction.Include);

        return !directRules.Any(l => l.Action == SelectionAction.Exclude)
            && (!hasIncludes || directRules.Any(l => l.Action == SelectionAction.Include));
    }

    private static bool MeetsConditions(Offer offer, IReadOnlyList<ApplicablePromotionItemRequest> basket)
    {
        if (offer.Conditions.Count == 0)
        {
            // Simple discounts use reward-scoped rules to identify discounted items.
            return MatchesItemRules(offer.Items.Where(i => i.Scope == ItemScope.Reward), basket).Any();
        }

        return offer.Conditions.All(condition =>
        {
            var matching = MatchesItemRules(condition.Items, basket).ToList();
            var quantity = matching.Sum(i => i.InvoiceQty);
            var spend = matching.Sum(i => i.UnitPrice * i.InvoiceQty);

            return (!condition.BuyQuantity.HasValue || quantity >= condition.BuyQuantity.Value)
                && (!condition.SpendAmount.HasValue || spend >= condition.SpendAmount.Value);
        });
    }

    private static IEnumerable<ApplicablePromotionItemRequest> MatchesItemRules(
        IEnumerable<OfferItem> rules, IReadOnlyList<ApplicablePromotionItemRequest> basket)
    {
        var activeRules = rules.Where(r => r.CancelledAtUtc is null).ToList();
        var includes = activeRules.Where(r => r.Action == SelectionAction.Include).ToList();
        var excludes = activeRules.Where(r => r.Action == SelectionAction.Exclude).ToList();

        return basket.Where(item =>
            (includes.Count == 0 || includes.Any(rule => Matches(rule, item)))
            && !excludes.Any(rule => Matches(rule, item)));
    }

    private static bool Matches(OfferItem rule, ApplicablePromotionItemRequest item) => rule.Level switch
    {
        ItemSelectionLevel.AllDepartments => true,
        ItemSelectionLevel.Item => !string.IsNullOrWhiteSpace(rule.Barcode)
            && string.Equals(rule.Barcode, item.Barcode, StringComparison.OrdinalIgnoreCase),
        // The external contract carries a barcode only, so hierarchy rules cannot
        // be evaluated safely without a product-catalog lookup.
        _ => false
    };

    private static ApplicablePromotionResponse? ToResponse(Offer offer)
    {
        // Nothing to tell the till if the offer never stated a discount.
        if (DescribeReward(offer.Reward) is not { } offerText) return null;

        var promotion = offer.Promotion!;
        return new ApplicablePromotionResponse
        {
            // Existing business identifiers are used rather than introducing a
            // second identifier scheme for this read-only integration.
            PromoId = promotion.PromotionNumber,
            PromoNo = $"[{promotion.PromotionNumber}] {promotion.Description}",
            SlabId = offer.OfferNumber,
            SlabNo = offer.OfferNumber.ToString(),
            SlabDesc = offer.Description,
            IsCrossCategory = 0,
            PromoOffers = [new ApplicablePromotionOfferResponse
            {
                OfferId = offer.OfferNumber,
                Offer = offerText
            }]
        };
    }

    /// <summary>
    /// How the reward reads to a till, or null when the offer states no terms. Null
    /// rather than a zero discount on purpose: telling a POS "Discount Percentage 0.00%"
    /// looks like a working answer and silently applies nothing, which is worse than
    /// reporting no applicable promotion.
    /// </summary>
    private static string? DescribeReward(OfferReward? reward)
    {
        if (reward is null) return null;

        if (reward.DiscountType == DiscountType.FreeItem)
        {
            return $"Free Item {reward.GiftItemDescription ?? reward.GiftItemId ?? string.Empty}".Trim();
        }

        return reward.DiscountValue is null
            ? null
            : DescribeDiscount(reward.DiscountType, reward.DiscountValue);
    }

    /// <summary>Wording follows the integration document, e.g. "Discount Percentage 15.00%".</summary>
    private static string DescribeDiscount(DiscountType type, decimal? value) => type switch
    {
        DiscountType.PercentOff => $"Discount Percentage {value ?? 0:0.00}%",
        DiscountType.AmountOff => $"Discount Amount {value ?? 0:0.00}",
        DiscountType.FixedPrice => $"Fixed Price {value ?? 0:0.00}",
        DiscountType.FreeItem => "Free Item",
        _ => "Promotion offer"
    };
}
