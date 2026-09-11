namespace PromoEngine.Domain.Promotions;

/// <summary>
/// A product row uploaded against an <see cref="Offer"/> so that an external point
/// of sale - EasyPOS and the like - can resolve a scanned barcode to that offer's
/// discount through <c>POST /api/External/get-applicable-promotions</c>.
///
/// An offer describes a discount the way a merchandiser thinks about it: a template,
/// conditions, a reward and merchandise hierarchy rules. A till has none of that
/// hierarchy, it has a barcode. This table is the flattened, barcode keyed list of
/// the products the offer covers, uploaded as a spreadsheet rather than derived, so
/// the retailer stays in control of exactly which items the POS sees.
///
/// Deliberately carries no discount columns. The offer's reward is the discount for
/// every row here - that is what "the offer is the discount data for its own sheet"
/// means - so a sheet can never disagree with the offer it belongs to.
/// </summary>
public class OfferProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OfferId { get; set; }
    public Offer? Offer { get; set; }

    // -----------------------------------------------------------------------
    // Product identity
    // -----------------------------------------------------------------------

    /// <summary>What the POS actually sends. The only mandatory column on the sheet.</summary>
    public string Barcode { get; set; } = string.Empty;

    public string? ItemId { get; set; }
    public string? StyleCode { get; set; }
    public string? ItemDescription { get; set; }
    public string? Department { get; set; }
    public string? Class { get; set; }
    public string? Subclass { get; set; }
    public string? Brand { get; set; }
    public string? VendorName { get; set; }
    public string? SupplierSite { get; set; }

    // -----------------------------------------------------------------------
    // Eligibility
    // -----------------------------------------------------------------------

    /// <summary>
    /// Include takes the discount, Exclude withholds it. An uploaded row is a
    /// discounted product unless the user says otherwise, so this defaults to
    /// Include and an Exclude row exists purely to carve a barcode back out of an
    /// otherwise included sheet.
    /// </summary>
    public SelectionAction Action { get; set; } = SelectionAction.Include;

    /// <summary>
    /// The <c>GetApplicablePromotions</c> column. Only rows flagged true are visible
    /// to the external API, so a sheet can carry the whole product range while
    /// exposing part of it.
    /// </summary>
    public bool GetApplicablePromotions { get; set; } = true;

    /// <summary>Matched against the request's PromotypeID. Blank matches any type.</summary>
    public string? PromoTypeId { get; set; }

    /// <summary>Matched against the request's SiteCode. Blank matches every site.</summary>
    public string? SiteCode { get; set; }

    /// <summary>Matched against the request's CustomerTier. Blank matches every tier.</summary>
    public string? CustomerTier { get; set; }

    // -----------------------------------------------------------------------
    // Provenance
    // -----------------------------------------------------------------------

    public string? SourceFileName { get; set; }
    public int? SourceRowNumber { get; set; }
    public string? UploadedBy { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
