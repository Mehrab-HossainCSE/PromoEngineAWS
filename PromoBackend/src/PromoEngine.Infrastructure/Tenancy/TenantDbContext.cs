using Microsoft.EntityFrameworkCore;
using PromoEngine.Domain.Promotions;
using PromoEngine.Infrastructure.Postgres;

namespace PromoEngine.Infrastructure.Tenancy;

/// <summary>
/// The per-tenant database. One physical database per customer, created by the
/// provisioner at subscription time, holding only that customer's promotion data.
/// </summary>
public class TenantDbContext(DbContextOptions<TenantDbContext> options) : DbContext(options)
{
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Offer> Offers => Set<Offer>();
    public DbSet<OfferCondition> Conditions => Set<OfferCondition>();
    public DbSet<OfferReward> Rewards => Set<OfferReward>();
    public DbSet<OfferItem> Items => Set<OfferItem>();
    public DbSet<OfferLocation> Locations => Set<OfferLocation>();

    /// <summary>Barcode keyed rows uploaded against an offer for the external POS API.</summary>
    public DbSet<OfferProduct> OfferProducts => Set<OfferProduct>();

    /// <summary>Stores every DateTimeOffset as UTC, which is all timestamptz can hold.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder b) => b.UseUtcDateTimeOffsets();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddCaseInsensitiveCollation();

        b.Entity<Promotion>(e =>
        {
            e.ToTable("Promotions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Description).HasMaxLength(400).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(320);
            e.Property(x => x.UpdatedBy).HasMaxLength(320);
            e.Ignore(x => x.StartDate);
            e.Ignore(x => x.EndDate);
            e.HasIndex(x => x.PromotionNumber).IsUnique();
            e.HasOne(x => x.Campaign).WithMany(c => c.Promotions)
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<OfferProduct>(e =>
        {
            e.ToTable("OfferProducts");
            e.HasKey(x => x.Id);
            // The barcode is what a till is matched on, and SQL Server matched it
            // case-insensitively. See PostgresConventions.
            e.Property(x => x.Barcode).HasMaxLength(64).IsRequired()
                .UseCollation(PostgresConventions.CaseInsensitiveCollation);
            e.Property(x => x.ItemId).HasMaxLength(64);
            e.Property(x => x.StyleCode).HasMaxLength(64);
            e.Property(x => x.ItemDescription).HasMaxLength(400);
            e.Property(x => x.Department).HasMaxLength(64);
            e.Property(x => x.Class).HasMaxLength(64);
            e.Property(x => x.Subclass).HasMaxLength(64);
            e.Property(x => x.Brand).HasMaxLength(64);
            e.Property(x => x.VendorName).HasMaxLength(200);
            e.Property(x => x.SupplierSite).HasMaxLength(64);
            e.Property(x => x.PromoTypeId).HasMaxLength(64);
            e.Property(x => x.SiteCode).HasMaxLength(64);
            e.Property(x => x.CustomerTier).HasMaxLength(64);
            e.Property(x => x.SourceFileName).HasMaxLength(260);
            e.Property(x => x.UploadedBy).HasMaxLength(320);
            // The external API resolves a scanned barcode on every basket line, so
            // the barcode lookup is the one that has to stay cheap.
            e.HasIndex(x => x.Barcode);
            e.HasIndex(x => new { x.OfferId, x.Barcode });
            e.HasOne(x => x.Offer).WithMany(o => o.Products)
                .HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Campaign>(e =>
        {
            e.ToTable("Campaigns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(10).IsRequired()
                .UseCollation(PostgresConventions.CaseInsensitiveCollation);
            e.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<Offer>(e =>
        {
            e.ToTable("Offers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Description).HasMaxLength(400).IsRequired();
            e.Property(x => x.TemplateCode).HasMaxLength(64).IsRequired()
                .UseCollation(PostgresConventions.CaseInsensitiveCollation);
            e.Property(x => x.Comments).HasMaxLength(2000);
            e.Property(x => x.CouponCode).HasMaxLength(64);
            e.Property(x => x.CustomerDescription).HasMaxLength(1000);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.Property(x => x.ApprovedBy).HasMaxLength(320);
            e.HasIndex(x => new { x.PromotionId, x.OfferNumber }).IsUnique();
            e.HasOne(x => x.Promotion).WithMany(p => p.Offers)
                .HasForeignKey(x => x.PromotionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Reward).WithOne(r => r.Offer!)
                .HasForeignKey<OfferReward>(r => r.OfferId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OfferCondition>(e =>
        {
            e.ToTable("OfferConditions");
            e.HasKey(x => x.Id);
            e.Property(x => x.BuyQuantity).HasPrecision(18, 4);
            e.Property(x => x.SpendAmount).HasPrecision(18, 4);
            e.Property(x => x.PriceRestrictionFrom).HasPrecision(18, 4);
            e.Property(x => x.PriceRestrictionTo).HasPrecision(18, 4);
            e.Property(x => x.UnitOfMeasure).HasMaxLength(20);
            e.Property(x => x.CurrencyCode).HasMaxLength(3);
            e.Property(x => x.PriceRestrictionCurrency).HasMaxLength(3);
            e.HasOne(x => x.Offer).WithMany(o => o.Conditions)
                .HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OfferReward>(e =>
        {
            e.ToTable("OfferRewards");
            e.HasKey(x => x.Id);
            e.Property(x => x.DiscountValue).HasPrecision(18, 4);
            e.Property(x => x.GetQuantity).HasPrecision(18, 4);
            e.Property(x => x.CurrencyCode).HasMaxLength(3);
            e.Property(x => x.GiftItemId).HasMaxLength(64);
            e.Property(x => x.GiftItemDescription).HasMaxLength(400);
            e.Property(x => x.SingleItemId).HasMaxLength(64);
            e.Property(x => x.SingleItemDescription).HasMaxLength(400);
        });

        b.Entity<OfferItem>(e =>
        {
            e.ToTable("OfferItems");
            e.HasKey(x => x.Id);
            e.Property(x => x.Department).HasMaxLength(64);
            e.Property(x => x.Class).HasMaxLength(64);
            e.Property(x => x.Subclass).HasMaxLength(64);
            e.Property(x => x.ItemId).HasMaxLength(64);
            e.Property(x => x.ItemDescription).HasMaxLength(400);
            e.Property(x => x.Barcode).HasMaxLength(64)
                .UseCollation(PostgresConventions.CaseInsensitiveCollation);
            e.Property(x => x.StyleCode).HasMaxLength(64);
            e.Property(x => x.VendorName).HasMaxLength(200);
            e.Property(x => x.ParentItemId).HasMaxLength(64);
            e.Property(x => x.DiffType).HasMaxLength(64);
            e.Property(x => x.DiffValue).HasMaxLength(64);
            e.Property(x => x.ItemListId).HasMaxLength(64);
            e.Property(x => x.SourceFileName).HasMaxLength(260);
            e.Property(x => x.SupplierSite).HasMaxLength(64);
            e.Property(x => x.Brand).HasMaxLength(64);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.Ignore(x => x.Summary);
            // Barcodes are looked up when resolving a scanned item to an offer, and
            // style codes when reconciling an uploaded list against the hierarchy.
            e.HasIndex(x => x.Barcode);
            e.HasIndex(x => x.StyleCode);
            e.HasOne(x => x.Offer).WithMany(o => o.Items)
                .HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.Cascade);
            // The condition relationship is optional and must not cascade, otherwise
            // SQL Server sees two cascade paths from Offers to OfferItems.
            e.HasOne(x => x.Condition).WithMany(c => c.Items)
                .HasForeignKey(x => x.ConditionId).OnDelete(DeleteBehavior.ClientCascade);
        });

        b.Entity<OfferLocation>(e =>
        {
            e.ToTable("OfferLocations");
            e.HasKey(x => x.Id);
            e.Property(x => x.ZoneGroup).HasMaxLength(64);
            e.Property(x => x.Zone).HasMaxLength(64);
            e.Property(x => x.LocationList).HasMaxLength(64);
            e.Property(x => x.Store).HasMaxLength(64);
            e.Property(x => x.StoreName).HasMaxLength(200);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.Ignore(x => x.Summary);
            e.HasOne(x => x.Offer).WithMany(o => o.Locations)
                .HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
