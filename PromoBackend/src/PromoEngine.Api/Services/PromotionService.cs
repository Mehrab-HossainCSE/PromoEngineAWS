using Microsoft.EntityFrameworkCore;
using PromoEngine.Api.Contracts;
using PromoEngine.Domain.Catalog;
using PromoEngine.Domain.Promotions;
using PromoEngine.Domain.Templates;
using PromoEngine.Infrastructure.Tenancy;

namespace PromoEngine.Api.Services;

public sealed record ServiceResult<T>(bool Success, T? Value, string? Error, Dictionary<string, string[]>? Errors = null)
{
    public static ServiceResult<T> Ok(T value) => new(true, value, null);
    public static ServiceResult<T> Fail(string error) => new(false, default, error);
    public static ServiceResult<T> Invalid(Dictionary<string, string[]> errors) => new(false, default, "Validation failed.", errors);
}

/// <summary>
/// All promotion reads and writes against the caller's own tenant database. Every
/// method opens a context through <see cref="ITenantDbContextFactory"/>, which
/// resolves the connection string set by the tenant middleware, so one tenant can
/// never touch another's data.
/// </summary>
public sealed class PromotionService(
    ITenantDbContextFactory contextFactory,
    OfferValidator validator,
    ILogger<PromotionService> logger)
{
    // -----------------------------------------------------------------------
    // Promotions
    // -----------------------------------------------------------------------

    private const string LikeEscape = "\\";

    /// <summary>
    /// Neutralises the LIKE wildcards in a user's search term. Contains did this for
    /// us; building the pattern by hand means a description containing % or _ would
    /// otherwise match far more than the user typed.
    /// </summary>
    private static string Escape(string term) => term
        .Replace(LikeEscape, LikeEscape + LikeEscape)
        .Replace("%", LikeEscape + "%")
        .Replace("_", LikeEscape + "_");

    public async Task<PagedResponse<PromotionSummaryResponse>> SearchAsync(PromotionSearchRequest request, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var query = db.Promotions
            .Include(p => p.Campaign)
            .Include(p => p.Offers)
            .AsNoTracking()
            .AsQueryable();

        if (request.PromotionNumber is { } number)
        {
            query = query.Where(p => p.PromotionNumber == number);
        }

        // ILike rather than Contains: SQL Server's default collation made a Contains
        // case-insensitive, so searching "winter" found "Winter clearance". PostgreSQL
        // compares text case-sensitively and refuses LIKE against a case-insensitive
        // collation, so ILIKE is the only way to keep the search behaving as it did.
        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            var pattern = $"%{Escape(request.Description)}%";
            query = query.Where(p => EF.Functions.ILike(p.Description, pattern, LikeEscape));
        }

        if (!string.IsNullOrWhiteSpace(request.OfferDescription))
        {
            var pattern = $"%{Escape(request.OfferDescription)}%";
            query = query.Where(p => p.Offers.Any(o => EF.Functions.ILike(o.Description, pattern, LikeEscape)));
        }

        if (!string.IsNullOrWhiteSpace(request.OfferType)
            && Enum.TryParse<OfferType>(request.OfferType, true, out var offerType))
        {
            query = query.Where(p => p.Offers.Any(o => o.Type == offerType));
        }

        if (!string.IsNullOrWhiteSpace(request.TemplateCode))
        {
            query = query.Where(p => p.Offers.Any(o => o.TemplateCode == request.TemplateCode));
        }

        if (request.StartDate is { } startDate)
        {
            query = query.Where(p => p.Offers.Any(o => o.StartDate >= startDate));
        }

        if (request.EndDate is { } endDate)
        {
            query = query.Where(p => p.Offers.Any(o => o.EndDate != null && o.EndDate <= endDate));
        }

        if (!string.IsNullOrWhiteSpace(request.Status)
            && Enum.TryParse<PromotionStatus>(request.Status, true, out var status))
        {
            query = query.Where(p => p.Status == status || p.Offers.Any(o => o.Status == status));
        }

        if (!string.IsNullOrWhiteSpace(request.ItemId))
        {
            query = query.Where(p => p.Offers.Any(o =>
                o.Items.Any(i => i.ItemId == request.ItemId || i.ParentItemId == request.ItemId)));
        }

        var total = await query.CountAsync(ct);

        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var page = Math.Max(request.Page, 1);

        var results = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResponse<PromotionSummaryResponse>
        {
            Items = results.Select(p => p.ToSummary()).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PromotionDetailResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();
        var promotion = await LoadPromotionAsync(db, id, tracking: false, ct);
        return promotion?.ToDetail();
    }

    public async Task<ServiceResult<PromotionDetailResponse>> CreatePromotionAsync(
        SavePromotionRequest request, string createdBy, int maxPromotions, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var count = await db.Promotions.CountAsync(ct);
        if (count >= maxPromotions)
        {
            return ServiceResult<PromotionDetailResponse>.Fail(
                $"Your plan allows {maxPromotions:N0} promotions. Upgrade to create more.");
        }

        var nextNumber = await NextPromotionNumberAsync(db, ct);

        var promotion = new Promotion
        {
            PromotionNumber = nextNumber,
            Description = request.Description.Trim(),
            CampaignId = request.CampaignId,
            CreatedBy = createdBy
        };

        db.Promotions.Add(promotion);
        await db.SaveChangesAsync(ct);

        var saved = await LoadPromotionAsync(db, promotion.Id, tracking: false, ct);
        return ServiceResult<PromotionDetailResponse>.Ok(saved!.ToDetail());
    }

    public async Task<ServiceResult<PromotionDetailResponse>> UpdatePromotionAsync(
        Guid id, SavePromotionRequest request, string updatedBy, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var promotion = await db.Promotions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (promotion is null)
        {
            return ServiceResult<PromotionDetailResponse>.Fail("Promotion not found.");
        }

        promotion.Description = request.Description.Trim();
        promotion.CampaignId = request.CampaignId;
        promotion.UpdatedBy = updatedBy;
        promotion.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        var saved = await LoadPromotionAsync(db, id, tracking: false, ct);
        return ServiceResult<PromotionDetailResponse>.Ok(saved!.ToDetail());
    }

    public async Task<bool> DeletePromotionAsync(Guid id, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var promotion = await db.Promotions
            .Include(p => p.Offers)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (promotion is null)
        {
            return false;
        }

        db.Promotions.Remove(promotion);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // -----------------------------------------------------------------------
    // Offers
    // -----------------------------------------------------------------------

    public async Task<ServiceResult<OfferResponse>> AddOfferAsync(
        Guid promotionId, SaveOfferRequest request, SubscriptionPlan plan, string? savedBy, CancellationToken ct)
    {
        var validation = validator.Validate(request, plan);
        if (!validation.IsValid)
        {
            return ServiceResult<OfferResponse>.Invalid(validation.Errors);
        }

        await using var db = contextFactory.CreateForCurrentTenant();

        var promotion = await db.Promotions
            .Include(p => p.Offers)
            .FirstOrDefaultAsync(p => p.Id == promotionId, ct);

        if (promotion is null)
        {
            return ServiceResult<OfferResponse>.Fail("Promotion not found.");
        }

        if (promotion.Offers.Count >= plan.MaxOffersPerPromotion)
        {
            return ServiceResult<OfferResponse>.Fail(
                $"Your plan allows {plan.MaxOffersPerPromotion} offers per promotion.");
        }

        var template = OfferTemplateCatalog.Find(request.TemplateCode)!;

        var offer = new Offer
        {
            PromotionId = promotionId,
            OfferNumber = promotion.Offers.Count == 0 ? 1 : promotion.Offers.Max(o => o.OfferNumber) + 1,
            Level = template.Level,
            Type = template.Type,
            TemplateCode = template.Code
        };

        db.Offers.Add(offer);
        ApplyOffer(db, offer, request, template, savedBy);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Added offer {Offer} ({Template}) to promotion {Promotion}",
            offer.OfferNumber, offer.TemplateCode, promotion.PromotionNumber);

        var saved = await LoadOfferAsync(db, offer.Id, tracking: false, ct);
        return ServiceResult<OfferResponse>.Ok(saved!.ToResponse());
    }

    public async Task<ServiceResult<OfferResponse>> UpdateOfferAsync(
        Guid offerId, SaveOfferRequest request, SubscriptionPlan plan, string? savedBy, CancellationToken ct)
    {
        var validation = validator.Validate(request, plan);
        if (!validation.IsValid)
        {
            return ServiceResult<OfferResponse>.Invalid(validation.Errors);
        }

        await using var db = contextFactory.CreateForCurrentTenant();

        var offer = await LoadOfferAsync(db, offerId, tracking: true, ct);
        if (offer is null)
        {
            return ServiceResult<OfferResponse>.Fail("Offer not found.");
        }

        if (offer.Status is PromotionStatus.Approved or PromotionStatus.Active or PromotionStatus.Completed)
        {
            return ServiceResult<OfferResponse>.Fail(
                $"The offer is {offer.Status} and cannot be edited. Move it back to Worksheet first.");
        }

        if (offer.Status == PromotionStatus.Cancelled)
        {
            return ServiceResult<OfferResponse>.Fail("A cancelled offer cannot be edited.");
        }

        var template = OfferTemplateCatalog.Find(request.TemplateCode)!;

        // The template drives the entire shape of conditions, reward and items, so a
        // change of template replaces those children wholesale.
        db.Items.RemoveRange(offer.Items);
        db.Conditions.RemoveRange(offer.Conditions);
        db.OfferProducts.RemoveRange(offer.Products);
        offer.Items.Clear();
        offer.Conditions.Clear();
        offer.Products.Clear();

        if (offer.Reward is not null)
        {
            db.Rewards.Remove(offer.Reward);
            offer.Reward = null;
        }

        offer.Level = template.Level;
        offer.Type = template.Type;
        offer.TemplateCode = template.Code;
        ApplyOffer(db, offer, request, template, savedBy);
        offer.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        var saved = await LoadOfferAsync(db, offerId, tracking: false, ct);
        return ServiceResult<OfferResponse>.Ok(saved!.ToResponse());
    }

    /// <summary>
    /// Clones an offer, which is how tiered offers are built (buy 2 get 10%, buy 3
    /// get 20%). Everything except the dates, coupon and description is copied.
    /// </summary>
    public async Task<ServiceResult<OfferResponse>> CreateFromExistingAsync(
        Guid sourceOfferId, CreateOfferFromExistingRequest request, SubscriptionPlan plan, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var source = await LoadOfferAsync(db, sourceOfferId, tracking: false, ct);
        if (source is null)
        {
            return ServiceResult<OfferResponse>.Fail("Source offer not found.");
        }

        var siblings = await db.Offers.Where(o => o.PromotionId == source.PromotionId).ToListAsync(ct);
        if (siblings.Count >= plan.MaxOffersPerPromotion)
        {
            return ServiceResult<OfferResponse>.Fail(
                $"Your plan allows {plan.MaxOffersPerPromotion} offers per promotion.");
        }

        var clone = new Offer
        {
            PromotionId = source.PromotionId,
            OfferNumber = siblings.Max(o => o.OfferNumber) + 1,
            Description = request.Description.Trim(),
            Level = source.Level,
            Type = source.Type,
            TemplateCode = source.TemplateCode,
            StartDate = request.StartDate,
            StartTime = request.StartTime,
            EndDate = request.EndDate,
            EndTime = request.EndTime,
            Comments = request.Comments ?? source.Comments,
            CouponCode = request.CouponCode,
            CouponCodeRequired = source.CouponCodeRequired,
            DistributionRule = source.DistributionRule,
            ExclusiveDiscount = source.ExclusiveDiscount,
            CustomerDescription = request.CustomerDescription ?? source.CustomerDescription,
            IsEmergency = source.IsEmergency,
            Status = PromotionStatus.Worksheet
        };

        foreach (var condition in source.Conditions.OrderBy(c => c.Sequence))
        {
            var clonedCondition = new OfferCondition
            {
                OfferId = clone.Id,
                Sequence = condition.Sequence,
                BuyQuantity = condition.BuyQuantity,
                UnitOfMeasure = condition.UnitOfMeasure,
                SpendAmount = condition.SpendAmount,
                CurrencyCode = condition.CurrencyCode,
                PriceRestriction = condition.PriceRestriction,
                PriceRestrictionFrom = condition.PriceRestrictionFrom,
                PriceRestrictionTo = condition.PriceRestrictionTo,
                PriceRestrictionCurrency = condition.PriceRestrictionCurrency
            };

            foreach (var item in source.Items.Where(i => i.ConditionId == condition.Id))
            {
                clone.Items.Add(CloneItem(item, clone.Id, clonedCondition.Id));
            }

            clone.Conditions.Add(clonedCondition);
        }

        foreach (var item in source.Items.Where(i => i.Scope == ItemScope.Reward))
        {
            clone.Items.Add(CloneItem(item, clone.Id, null));
        }

        if (source.Reward is not null)
        {
            clone.Reward = new OfferReward
            {
                OfferId = clone.Id,
                DiscountType = source.Reward.DiscountType,
                DiscountValue = source.Reward.DiscountValue,
                CurrencyCode = source.Reward.CurrencyCode,
                AppliesToAllCurrencies = source.Reward.AppliesToAllCurrencies,
                ApplyTo = source.Reward.ApplyTo,
                ApplyDiscountUpTo = source.Reward.ApplyDiscountUpTo,
                GetQuantity = source.Reward.GetQuantity,
                GiftItemId = source.Reward.GiftItemId,
                GiftItemDescription = source.Reward.GiftItemDescription,
                SingleItemId = source.Reward.SingleItemId,
                SingleItemDescription = source.Reward.SingleItemDescription
            };
        }

        foreach (var product in source.Products)
        {
            clone.Products.Add(new OfferProduct
            {
                OfferId = clone.Id,
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
                Action = product.Action,
                GetApplicablePromotions = product.GetApplicablePromotions,
                PromoTypeId = product.PromoTypeId,
                SiteCode = product.SiteCode,
                CustomerTier = product.CustomerTier,
                SourceFileName = product.SourceFileName,
                SourceRowNumber = product.SourceRowNumber,
                UploadedBy = product.UploadedBy
            });
        }

        if (request.CopyLocations)
        {
            foreach (var location in source.Locations.Where(l => l.CancelledAtUtc is null))
            {
                clone.Locations.Add(new OfferLocation
                {
                    OfferId = clone.Id,
                    Action = location.Action,
                    Level = location.Level,
                    ZoneGroup = location.ZoneGroup,
                    Zone = location.Zone,
                    LocationList = location.LocationList,
                    Store = location.Store,
                    StoreName = location.StoreName
                });
            }
        }

        db.Offers.Add(clone);
        await db.SaveChangesAsync(ct);

        var saved = await LoadOfferAsync(db, clone.Id, tracking: false, ct);
        return ServiceResult<OfferResponse>.Ok(saved!.ToResponse());
    }

    public async Task<int> MassUpdateOffersAsync(MassUpdateOffersRequest request, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var offers = await db.Offers.Where(o => request.OfferIds.Contains(o.Id)).ToListAsync(ct);

        foreach (var offer in offers)
        {
            if (request.StartDate is { } start) offer.StartDate = start;
            if (request.StartTime is { } startTime) offer.StartTime = startTime;
            if (request.EndDate is { } end) offer.EndDate = end;
            if (request.EndTime is { } endTime) offer.EndTime = endTime;
            if (!string.IsNullOrWhiteSpace(request.CouponCode)) offer.CouponCode = request.CouponCode;
            if (!string.IsNullOrWhiteSpace(request.Comments)) offer.Comments = request.Comments;
            if (!string.IsNullOrWhiteSpace(request.CustomerDescription)) offer.CustomerDescription = request.CustomerDescription;

            // The clear flags win over the value fields, matching the mass update dialog.
            if (request.ClearEndDate) { offer.EndDate = null; offer.EndTime = null; }
            if (request.ClearCouponCode) { offer.CouponCode = null; offer.CouponCodeRequired = false; }
            if (request.ClearComments) offer.Comments = null;
            if (request.ClearCustomerDescription) offer.CustomerDescription = null;

            offer.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return offers.Count;
    }

    /// <summary>Moves offers through the worksheet -> submitted -> approved flow.</summary>
    public async Task<ServiceResult<int>> ChangeStatusAsync(
        IReadOnlyCollection<Guid> offerIds, PromotionStatus target, string actingUser, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var offers = await db.Offers
            .Include(o => o.Promotion)
            .Where(o => offerIds.Contains(o.Id))
            .ToListAsync(ct);

        if (offers.Count == 0)
        {
            return ServiceResult<int>.Fail("No matching offers.");
        }

        foreach (var offer in offers)
        {
            if (offer.Status == PromotionStatus.Cancelled)
            {
                return ServiceResult<int>.Fail($"Offer {offer.OfferNumber} is cancelled and cannot change status.");
            }

            switch (target)
            {
                case PromotionStatus.Approved:
                    if (offer.Status is not (PromotionStatus.Worksheet or PromotionStatus.Submitted))
                    {
                        return ServiceResult<int>.Fail($"Offer {offer.OfferNumber} is {offer.Status} and cannot be approved.");
                    }

                    offer.ApprovedBy = actingUser;
                    offer.ApprovedAtUtc = DateTimeOffset.UtcNow;

                    // An approved offer whose start date has arrived goes straight to Active.
                    offer.Status = offer.StartDate.Date <= DateTimeOffset.UtcNow.Date
                        ? PromotionStatus.Active
                        : PromotionStatus.Approved;
                    break;

                case PromotionStatus.Submitted:
                    if (offer.Status != PromotionStatus.Worksheet)
                    {
                        return ServiceResult<int>.Fail($"Offer {offer.OfferNumber} is {offer.Status} and cannot be submitted.");
                    }

                    offer.Status = PromotionStatus.Submitted;
                    break;

                case PromotionStatus.Worksheet:
                    if (offer.Status is PromotionStatus.Active or PromotionStatus.Completed)
                    {
                        return ServiceResult<int>.Fail($"Offer {offer.OfferNumber} is {offer.Status} and cannot return to worksheet.");
                    }

                    offer.Status = PromotionStatus.Worksheet;
                    offer.ApprovedBy = null;
                    offer.ApprovedAtUtc = null;
                    break;

                case PromotionStatus.Rejected:
                    offer.Status = PromotionStatus.Rejected;
                    break;

                default:
                    return ServiceResult<int>.Fail($"Unsupported status transition to {target}.");
            }

            offer.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        // The promotion reflects the least advanced state of its offers.
        foreach (var promotion in offers.Select(o => o.Promotion).Where(p => p is not null).Distinct()!)
        {
            var all = await db.Offers.Where(o => o.PromotionId == promotion!.Id).ToListAsync(ct);
            promotion!.Status = all.Count == 0 ? PromotionStatus.Worksheet : all.Min(o => o.Status);
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<int>.Ok(offers.Count);
    }

    public async Task<ServiceResult<int>> CancelOffersAsync(
        IReadOnlyCollection<Guid> offerIds, string reason, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var offers = await db.Offers.Where(o => offerIds.Contains(o.Id)).ToListAsync(ct);
        if (offers.Count == 0)
        {
            return ServiceResult<int>.Fail("No matching offers.");
        }

        // Per the promotions guide, cancelling is only available for an active offer.
        var notActive = offers.Where(o => o.Status != PromotionStatus.Active).ToList();
        if (notActive.Count > 0)
        {
            return ServiceResult<int>.Fail(
                $"Only active offers can be cancelled. Offer {notActive[0].OfferNumber} is {notActive[0].Status}.");
        }

        foreach (var offer in offers)
        {
            offer.Status = PromotionStatus.Cancelled;
            offer.CancelReason = reason;
            offer.CancelledAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<int>.Ok(offers.Count);
    }

    public async Task<ServiceResult<int>> CancelItemsAsync(
        Guid offerId, IReadOnlyCollection<Guid> itemIds, string reason, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var offer = await db.Offers.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == offerId, ct);
        if (offer is null)
        {
            return ServiceResult<int>.Fail("Offer not found.");
        }

        if (offer.Status != PromotionStatus.Active)
        {
            return ServiceResult<int>.Fail("Items can only be cancelled from an active offer.");
        }

        var items = offer.Items.Where(i => itemIds.Contains(i.Id) && i.CancelledAtUtc is null).ToList();
        foreach (var item in items)
        {
            item.CancelReason = reason;
            item.CancelledAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<int>.Ok(items.Count);
    }

    public async Task<bool> DeleteOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var offers = await db.Offers.Where(o => offerIds.Contains(o.Id)).ToListAsync(ct);
        if (offers.Count == 0)
        {
            return false;
        }

        db.Offers.RemoveRange(offers);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // -----------------------------------------------------------------------
    // Locations
    // -----------------------------------------------------------------------

    public async Task<ServiceResult<IReadOnlyList<OfferLocationResponse>>> AddLocationsAsync(
        Guid offerId, IReadOnlyCollection<SaveOfferLocationRequest> requests, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var offer = await db.Offers.Include(o => o.Locations).FirstOrDefaultAsync(o => o.Id == offerId, ct);
        if (offer is null)
        {
            return ServiceResult<IReadOnlyList<OfferLocationResponse>>.Fail("Offer not found.");
        }

        foreach (var request in requests)
        {
            var level = Mapping.ParseEnum(request.Level, LocationLevel.Store);

            var missing = level switch
            {
                LocationLevel.Zone => string.IsNullOrWhiteSpace(request.Zone) ? "Zone is required." : null,
                LocationLevel.LocationList => string.IsNullOrWhiteSpace(request.LocationList) ? "Location list is required." : null,
                LocationLevel.Store => string.IsNullOrWhiteSpace(request.Store) ? "Store is required." : null,
                _ => null
            };

            if (missing is not null)
            {
                return ServiceResult<IReadOnlyList<OfferLocationResponse>>.Fail(missing);
            }

            db.Locations.Add(new OfferLocation
            {
                OfferId = offer.Id,
                Action = Mapping.ParseEnum(request.Action, SelectionAction.Include),
                Level = level,
                ZoneGroup = request.ZoneGroup,
                Zone = request.Zone,
                LocationList = request.LocationList,
                Store = request.Store,
                StoreName = request.StoreName
            });
        }

        await db.SaveChangesAsync(ct);

        var saved = await db.Locations
            .AsNoTracking()
            .Where(l => l.OfferId == offerId)
            .ToListAsync(ct);

        return ServiceResult<IReadOnlyList<OfferLocationResponse>>.Ok(
            saved.Select(l => l.ToResponse()).ToList());
    }

    /// <summary>Copies one offer's locations onto other offers in the same promotion.</summary>
    public async Task<ServiceResult<int>> CopyLocationsAsync(
        Guid sourceOfferId, IReadOnlyCollection<Guid> targetOfferIds, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var source = await db.Offers.Include(o => o.Locations).FirstOrDefaultAsync(o => o.Id == sourceOfferId, ct);
        if (source is null)
        {
            return ServiceResult<int>.Fail("Source offer not found.");
        }

        var targets = await db.Offers
            .Include(o => o.Locations)
            .Where(o => targetOfferIds.Contains(o.Id) && o.Id != sourceOfferId)
            .ToListAsync(ct);

        var outOfPromotion = targets.Where(t => t.PromotionId != source.PromotionId).ToList();
        if (outOfPromotion.Count > 0)
        {
            return ServiceResult<int>.Fail("Locations can only be copied to offers in the same promotion.");
        }

        var copied = 0;
        foreach (var target in targets)
        {
            foreach (var location in source.Locations.Where(l => l.CancelledAtUtc is null))
            {
                var duplicate = target.Locations.Any(existing =>
                    existing.Level == location.Level &&
                    existing.Action == location.Action &&
                    existing.Zone == location.Zone &&
                    existing.ZoneGroup == location.ZoneGroup &&
                    existing.LocationList == location.LocationList &&
                    existing.Store == location.Store);

                if (duplicate)
                {
                    continue;
                }

                var added = new OfferLocation
                {
                    OfferId = target.Id,
                    Action = location.Action,
                    Level = location.Level,
                    ZoneGroup = location.ZoneGroup,
                    Zone = location.Zone,
                    LocationList = location.LocationList,
                    Store = location.Store,
                    StoreName = location.StoreName
                };

                // Track through the DbSet, but keep the in-memory collection current
                // so the duplicate check sees rows added earlier in this same loop.
                db.Locations.Add(added);
                target.Locations.Add(added);
                copied++;
            }
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<int>.Ok(copied);
    }

    public async Task<ServiceResult<int>> CancelLocationsAsync(
        Guid offerId, IReadOnlyCollection<Guid> locationIds, string reason, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var offer = await db.Offers.Include(o => o.Locations).FirstOrDefaultAsync(o => o.Id == offerId, ct);
        if (offer is null)
        {
            return ServiceResult<int>.Fail("Offer not found.");
        }

        if (offer.Status != PromotionStatus.Active)
        {
            return ServiceResult<int>.Fail("Locations can only be cancelled from an active offer.");
        }

        var locations = offer.Locations.Where(l => locationIds.Contains(l.Id) && l.CancelledAtUtc is null).ToList();
        foreach (var location in locations)
        {
            location.CancelReason = reason;
            location.CancelledAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<int>.Ok(locations.Count);
    }

    public async Task<bool> DeleteLocationsAsync(Guid offerId, IReadOnlyCollection<Guid> locationIds, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var locations = await db.Locations
            .Where(l => l.OfferId == offerId && locationIds.Contains(l.Id))
            .ToListAsync(ct);

        if (locations.Count == 0)
        {
            return false;
        }

        db.Locations.RemoveRange(locations);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // -----------------------------------------------------------------------
    // Campaigns
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();
        var campaigns = await db.Campaigns.AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct);
        return campaigns.Select(c => c.ToResponse()).ToList();
    }

    public async Task<ServiceResult<CampaignResponse>> CreateCampaignAsync(SaveCampaignRequest request, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Campaigns.AnyAsync(c => c.Code == code, ct))
        {
            return ServiceResult<CampaignResponse>.Fail($"Campaign '{code}' already exists.");
        }

        var campaign = new Campaign { Code = code, Description = request.Description.Trim() };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);

        return ServiceResult<CampaignResponse>.Ok(campaign.ToResponse());
    }

    public async Task<ServiceResult<bool>> DeleteCampaignAsync(Guid id, CancellationToken ct)
    {
        await using var db = contextFactory.CreateForCurrentTenant();

        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return ServiceResult<bool>.Fail("Campaign not found.");
        }

        // Removing a campaign is not allowed while a promotion still references it.
        if (await db.Promotions.AnyAsync(p => p.CampaignId == id, ct))
        {
            return ServiceResult<bool>.Fail("This campaign is still in use by a promotion.");
        }

        db.Campaigns.Remove(campaign);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes the request onto the offer and registers its conditions, items and
    /// reward with the context.
    ///
    /// The children are added to the DbSets explicitly rather than to the offer's
    /// navigation collections: their Guid keys are assigned on the client, so an
    /// entity discovered through the navigation of an already tracked offer would be
    /// treated as Modified and EF would issue an UPDATE against a row that does not
    /// exist yet.
    /// </summary>
    private static void ApplyOffer(
        TenantDbContext db, Offer offer, SaveOfferRequest request, OfferTemplate template, string? uploadedBy)
    {
        offer.Description = request.Description.Trim();
        offer.StartDate = request.StartDate;
        offer.StartTime = request.StartTime;
        offer.EndDate = request.EndDate;
        offer.EndTime = request.EndTime;
        offer.Comments = request.Comments;
        offer.CouponCode = request.CouponCode;
        offer.CouponCodeRequired = request.CouponCodeRequired && !string.IsNullOrWhiteSpace(request.CouponCode);
        offer.ExclusiveDiscount = request.ExclusiveDiscount;
        offer.CustomerDescription = request.CustomerDescription;
        offer.IsEmergency = request.IsEmergency;

        offer.DistributionRule = template.AllowsDistributionRule
            ? Mapping.ParseEnum(request.DistributionRule, DistributionRule.BothBuyAndGetItems)
            : DistributionRule.NotApplicable;

        var sequence = 1;
        foreach (var conditionRequest in request.Conditions.OrderBy(c => c.Sequence))
        {
            var condition = new OfferCondition
            {
                OfferId = offer.Id,
                Sequence = sequence++,
                BuyQuantity = template.ConditionKind == ConditionKind.BuyQuantity ? conditionRequest.BuyQuantity : null,
                UnitOfMeasure = template.ConditionKind == ConditionKind.BuyQuantity ? conditionRequest.UnitOfMeasure : null,
                SpendAmount = template.ConditionKind == ConditionKind.SpendAmount ? conditionRequest.SpendAmount : null,
                CurrencyCode = conditionRequest.CurrencyCode,
                PriceRestriction = Mapping.ParseEnum(conditionRequest.PriceRestriction, PriceRestrictionOperator.None),
                PriceRestrictionFrom = conditionRequest.PriceRestrictionFrom,
                PriceRestrictionTo = conditionRequest.PriceRestrictionTo,
                PriceRestrictionCurrency = conditionRequest.PriceRestrictionCurrency
            };

            if (template.HasConditionItems)
            {
                foreach (var itemRequest in conditionRequest.Items)
                {
                    db.Items.Add(BuildItem(itemRequest, offer.Id, condition.Id, ItemScope.Condition));
                }
            }

            db.Conditions.Add(condition);
        }

        if (template.RewardItemMode != RewardItemMode.None)
        {
            foreach (var itemRequest in request.RewardItems)
            {
                db.Items.Add(BuildItem(itemRequest, offer.Id, null, ItemScope.Reward));
            }
        }

        var rewardRequest = request.Reward!;
        db.Rewards.Add(new OfferReward
        {
            OfferId = offer.Id,
            DiscountType = template.RewardKind == RewardKind.GiftItem
                ? DiscountType.FreeItem
                : Mapping.ParseEnum(rewardRequest.DiscountType, DiscountType.PercentOff),
            DiscountValue = template.RewardKind == RewardKind.GiftItem ? null : rewardRequest.DiscountValue,
            CurrencyCode = rewardRequest.AppliesToAllCurrencies ? null : rewardRequest.CurrencyCode,
            AppliesToAllCurrencies = rewardRequest.AppliesToAllCurrencies,
            ApplyTo = template.AllowsApplyTo
                ? Mapping.ParseEnum(rewardRequest.ApplyTo, ApplyToPriceType.RegularAndClearance)
                : ApplyToPriceType.RegularAndClearance,
            ApplyDiscountUpTo = template.AllowsApplyDiscountUpTo ? rewardRequest.ApplyDiscountUpTo : null,
            GetQuantity = rewardRequest.GetQuantity,
            GiftItemId = template.RewardKind == RewardKind.GiftItem ? rewardRequest.GiftItemId : null,
            GiftItemDescription = template.RewardKind == RewardKind.GiftItem ? rewardRequest.GiftItemDescription : null,
            SingleItemId = template.ConditionSingleItem ? rewardRequest.SingleItemId : null,
            SingleItemDescription = template.ConditionSingleItem ? rewardRequest.SingleItemDescription : null
        });

        // The uploaded sheet is the offer's own point of sale data, so it is written
        // with the offer rather than through a separate call. Whoever is saving the
        // offer is who uploaded it.
        var uploadedAt = DateTimeOffset.UtcNow;

        foreach (var product in request.Products)
        {
            db.OfferProducts.Add(new OfferProduct
            {
                OfferId = offer.Id,
                Barcode = product.Barcode.Trim(),
                ItemId = product.ItemId,
                StyleCode = product.StyleCode,
                ItemDescription = product.ItemDescription,
                Department = product.Department,
                Class = product.Class,
                Subclass = product.Subclass,
                Brand = product.Brand,
                VendorName = product.VendorName,
                SupplierSite = product.SupplierSite,
                Action = Mapping.ParseEnum(product.Action, SelectionAction.Include),
                GetApplicablePromotions = product.GetApplicablePromotions,
                PromoTypeId = product.PromoTypeId,
                SiteCode = product.SiteCode,
                CustomerTier = product.CustomerTier,
                SourceFileName = product.SourceFileName,
                SourceRowNumber = product.RowNumber,
                UploadedBy = uploadedBy,
                UploadedAtUtc = uploadedAt
            });
        }
    }

    private static OfferItem BuildItem(SaveOfferItemRequest request, Guid offerId, Guid? conditionId, ItemScope scope) => new()
    {
        OfferId = offerId,
        ConditionId = conditionId,
        Scope = scope,
        Action = Mapping.ParseEnum(request.Action, SelectionAction.Include),
        Level = Mapping.ParseEnum(request.Level, ItemSelectionLevel.AllDepartments),
        Department = request.Department,
        Class = request.Class,
        Subclass = request.Subclass,
        ItemId = request.ItemId,
        ItemDescription = request.ItemDescription,
        Barcode = request.Barcode,
        StyleCode = request.StyleCode,
        VendorName = request.VendorName,
        SourceRowNumber = request.SourceRowNumber,
        ParentItemId = request.ParentItemId,
        DiffType = request.DiffType,
        DiffValue = request.DiffValue,
        ItemListId = request.ItemListId,
        SourceFileName = request.SourceFileName,
        SupplierSite = request.SupplierSite,
        Brand = request.Brand
    };

    private static OfferItem CloneItem(OfferItem source, Guid offerId, Guid? conditionId) => new()
    {
        OfferId = offerId,
        ConditionId = conditionId,
        Scope = source.Scope,
        Action = source.Action,
        Level = source.Level,
        Department = source.Department,
        Class = source.Class,
        Subclass = source.Subclass,
        ItemId = source.ItemId,
        ItemDescription = source.ItemDescription,
        Barcode = source.Barcode,
        StyleCode = source.StyleCode,
        VendorName = source.VendorName,
        SourceRowNumber = source.SourceRowNumber,
        ParentItemId = source.ParentItemId,
        DiffType = source.DiffType,
        DiffValue = source.DiffValue,
        ItemListId = source.ItemListId,
        SourceFileName = source.SourceFileName,
        SupplierSite = source.SupplierSite,
        Brand = source.Brand
    };

    private static async Task<int> NextPromotionNumberAsync(TenantDbContext db, CancellationToken ct)
    {
        var highest = await db.Promotions
            .OrderByDescending(p => p.PromotionNumber)
            .Select(p => (int?)p.PromotionNumber)
            .FirstOrDefaultAsync(ct);

        return (highest ?? 1000) + 1;
    }

    private static Task<Promotion?> LoadPromotionAsync(TenantDbContext db, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.Promotions
            .Include(p => p.Campaign)
            .Include(p => p.Offers).ThenInclude(o => o.Conditions)
            .Include(p => p.Offers).ThenInclude(o => o.Reward)
            .Include(p => p.Offers).ThenInclude(o => o.Items)
            .Include(p => p.Offers).ThenInclude(o => o.Locations)
            .Include(p => p.Offers).ThenInclude(o => o.Products)
            .AsQueryable();

        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return query.FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    private static Task<Offer?> LoadOfferAsync(TenantDbContext db, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.Offers
            .Include(o => o.Conditions)
            .Include(o => o.Reward)
            .Include(o => o.Items)
            .Include(o => o.Locations)
            .Include(o => o.Products)
            .AsQueryable();

        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return query.FirstOrDefaultAsync(o => o.Id == id, ct);
    }
}

