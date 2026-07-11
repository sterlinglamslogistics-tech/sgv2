using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>, IDataProtectionKeyContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    // Data Protection keys live in the DB so antiforgery tokens, auth & session cookies survive
    // redeploys/cold starts on ephemeral hosting (Render free tier wipes the container filesystem).
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductTag> ProductTags => Set<ProductTag>();
    public DbSet<ProductAttribute> ProductAttributes => Set<ProductAttribute>();
    public DbSet<ProductAttributeValue> ProductAttributeValues => Set<ProductAttributeValue>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<StoreInventory> StoreInventories => Set<StoreInventory>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Register> Registers => Set<Register>();
    public DbSet<TillSession> TillSessions => Set<TillSession>();
    public DbSet<ParkedSale> ParkedSales => Set<ParkedSale>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundItem> RefundItems => Set<RefundItem>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferItem> StockTransferItems => Set<StockTransferItem>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<StockAdjustmentLine> StockAdjustmentLines => Set<StockAdjustmentLine>();
    public DbSet<StockTake> StockTakes => Set<StockTake>();
    public DbSet<StockTakeLine> StockTakeLines => Set<StockTakeLine>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<OrderNote> OrderNotes => Set<OrderNote>();
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<ProductReview> ProductReviews => Set<ProductReview>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<DiscountCode> DiscountCodes => Set<DiscountCode>();
    public DbSet<DiscountCategory> DiscountCategories => Set<DiscountCategory>();
    public DbSet<DiscountProduct> DiscountProducts => Set<DiscountProduct>();
    public DbSet<PosDiscountReason> PosDiscountReasons => Set<PosDiscountReason>();
    public DbSet<PosDiscountPreset> PosDiscountPresets => Set<PosDiscountPreset>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<NewsletterSubscriber> NewsletterSubscribers => Set<NewsletterSubscriber>();
    public DbSet<Models.Domain.UserStore> UserStores => Set<Models.Domain.UserStore>();
    public DbSet<LoyaltyAccount> LoyaltyAccounts => Set<LoyaltyAccount>();
    public DbSet<PointsLedgerEntry> PointsLedgerEntries => Set<PointsLedgerEntry>();
    public DbSet<GiftCard> GiftCards => Set<GiftCard>();
    public DbSet<GiftCardTransaction> GiftCardTransactions => Set<GiftCardTransaction>();
    public DbSet<BlogPost> BlogPosts => Set<BlogPost>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<SocialPost> SocialPosts => Set<SocialPost>();
    public DbSet<MarketingSuppression> MarketingSuppressions => Set<MarketingSuppression>();
    public DbSet<Automation> Automations => Set<Automation>();
    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<BackInStockRequest> BackInStockRequests => Set<BackInStockRequest>();
    public DbSet<AbandonedCart> AbandonedCarts => Set<AbandonedCart>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ─── Users: enforce unique email ────────────────────────────────────
        // Identity only makes usernames unique, not emails — which let a POS guest shell be
        // created with an existing customer's email, breaking login (FindByEmailAsync = SingleOrDefault).
        // Make the default EmailIndex UNIQUE, but only for real emails: guest/POS shells with a
        // NULL/empty email are exempt (Postgres partial index), so they can still be created.
        builder.Entity<ApplicationUser>(e =>
            e.HasIndex(u => u.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique()
                .HasFilter("\"NormalizedEmail\" IS NOT NULL AND \"NormalizedEmail\" <> ''"));

        // ─── Abandoned carts ────────────────────────────────────────────────
        builder.Entity<AbandonedCart>(e =>
        {
            e.HasIndex(a => a.Email).IsUnique();   // one snapshot per email (upserted each checkout)
            e.HasIndex(a => a.Token).IsUnique();
            e.HasIndex(a => new { a.RecoveredAt, a.EmailedAt, a.CreatedAt }); // sweep filter
            e.Property(a => a.Subtotal).HasPrecision(18, 2);
        });

        // ─── Back-in-stock requests ─────────────────────────────────────────
        builder.Entity<BackInStockRequest>(e =>
        {
            e.HasIndex(r => new { r.ProductId, r.NotifiedAt }); // sweep: pending per product
            // One pending request per product+email (a customer can't queue duplicates).
            e.HasIndex(r => new { r.ProductId, r.Email })
             .IsUnique().HasFilter("\"NotifiedAt\" IS NULL");
            e.HasOne(r => r.Product).WithMany().HasForeignKey(r => r.ProductId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── Loyalty ────────────────────────────────────────────────────────
        builder.Entity<LoyaltyAccount>(e =>
        {
            e.HasIndex(a => a.UserId).IsUnique();
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PointsLedgerEntry>(e =>
        {
            e.HasOne(p => p.Account).WithMany(a => a.Entries).HasForeignKey(p => p.LoyaltyAccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Order).WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.SetNull);
            // One accrual per order — makes point-awarding idempotent across the callback + webhook.
            e.HasIndex(p => p.OrderId).IsUnique().HasFilter("\"OrderId\" IS NOT NULL");
        });

        // ─── Gift cards ─────────────────────────────────────────────────────
        builder.Entity<GiftCard>(e =>
        {
            e.HasIndex(g => g.Code).IsUnique();
            e.Property(g => g.InitialAmount).HasColumnType("numeric(12,2)");
            e.Property(g => g.Balance).HasColumnType("numeric(12,2)");
        });
        builder.Entity<GiftCardTransaction>(e =>
        {
            e.HasOne(t => t.GiftCard).WithMany(g => g.Transactions).HasForeignKey(t => t.GiftCardId).OnDelete(DeleteBehavior.Cascade);
            e.Property(t => t.Amount).HasColumnType("numeric(12,2)");
            e.HasIndex(t => t.OrderId);
        });

        // ─── Marketing (campaigns) ──────────────────────────────────────────
        builder.Entity<Campaign>(e =>
        {
            e.Property(c => c.AudienceMinSpend).HasColumnType("numeric(12,2)");
        });
        builder.Entity<CampaignRecipient>(e =>
        {
            e.HasOne(r => r.Campaign).WithMany(c => c.Recipients).HasForeignKey(r => r.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.CampaignId, r.Status });
        });
        builder.Entity<MarketingSuppression>(e =>
        {
            e.HasIndex(s => s.Email).IsUnique();
        });
        builder.Entity<AutomationRun>(e =>
        {
            e.HasOne(r => r.Automation).WithMany(a => a.Runs).HasForeignKey(r => r.AutomationId).OnDelete(DeleteBehavior.Cascade);
            // One enrolment per customer per automation.
            e.HasIndex(r => new { r.AutomationId, r.Email }).IsUnique();
            e.HasIndex(r => new { r.Status, r.RunAt });
        });
        builder.Entity<Campaign>(e =>
        {
            e.HasOne(c => c.Segment).WithMany().HasForeignKey(c => c.SegmentId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<Referral>(e =>
        {
            e.HasOne(r => r.Referrer).WithMany().HasForeignKey(r => r.ReferrerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Referee).WithMany().HasForeignKey(r => r.RefereeUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => r.RefereeUserId).IsUnique();   // a person is referred at most once
            e.HasIndex(r => r.Status);
        });
        builder.Entity<ApplicationUser>(e =>
        {
            e.HasIndex(u => u.ReferralCode).IsUnique().HasFilter("\"ReferralCode\" IS NOT NULL");
        });

        // ─── Journal (blog / lookbook) ──────────────────────────────────────
        builder.Entity<BlogPost>(e =>
        {
            e.HasIndex(b => b.Slug).IsUnique();
            e.HasIndex(b => new { b.IsPublished, b.PublishedAt });
        });

        // ─── Product ────────────────────────────────────────────────────────
        builder.Entity<Product>(e =>
        {
            e.HasIndex(p => p.Slug).IsUnique();
            // Unique only for real codes — products created without an external code
            // store "" and must not collide with each other.
            e.HasIndex(p => p.ExternalCode).IsUnique().HasFilter("\"ExternalCode\" <> ''");
            // Barcode must be unique among real (non-blank) codes so a scan resolves to exactly
            // one product. Sku is indexed (not unique) for fast scan/lookup; both columns are
            // hit by the barcode-scan ScanLookup query on every till/stock scan.
            e.HasIndex(p => p.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL AND \"Barcode\" <> ''");
            e.HasIndex(p => p.Sku);
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.ToTable(t => t.HasCheckConstraint("CK_Products_Price_NonNegative", "\"Price\" >= 0"));

            e.HasOne(p => p.Category)
             .WithMany(c => c.Products)
             .HasForeignKey(p => p.CategoryId);

            e.HasMany(p => p.Tags)
             .WithMany(t => t.Products)
             .UsingEntity("ProductProductTag");
        });

        // ─── Category ───────────────────────────────────────────────────────
        builder.Entity<Category>(e =>
        {
            e.HasIndex(c => c.Slug).IsUnique();
            e.HasOne(c => c.Parent)
             .WithMany(c => c.Children)
             .HasForeignKey(c => c.ParentId)
             .IsRequired(false);
        });

        // ─── Store ──────────────────────────────────────────────────────────
        builder.Entity<Store>(e =>
        {
            e.HasIndex(s => s.Slug).IsUnique();
        });

        // ─── StoreInventory ─────────────────────────────────────────────────
        builder.Entity<StoreInventory>(e =>
        {
            // Variant-level stock: one row per (product, store) product-pool (ProductVariantId NULL),
            // plus one row per (product, store, variant). Two partial unique indexes because Postgres
            // treats NULLs as distinct, so a single combined unique index wouldn't constrain the
            // null-variant (pool) row.
            e.HasIndex(si => new { si.ProductId, si.StoreId })
                .IsUnique().HasFilter("\"ProductVariantId\" IS NULL");
            e.HasIndex(si => new { si.ProductId, si.StoreId, si.ProductVariantId })
                .IsUnique().HasFilter("\"ProductVariantId\" IS NOT NULL");
            e.HasOne(si => si.ProductVariant).WithMany().HasForeignKey(si => si.ProductVariantId)
                .OnDelete(DeleteBehavior.Cascade);
            // Optimistic concurrency: map Postgres' built-in xmin system column as a shadow
            // row-version property so concurrent updates to the same row (e.g. POS sale vs
            // transfer dispatch) throw DbUpdateConcurrencyException instead of silently overwriting.
            // Npgsql-only: xmin is a Postgres system column; mapping it on SQLite (the test harness)
            // would create a NOT NULL column with no default and break inserts.
            if (Database.IsNpgsql())
            {
                e.Property<uint>("xmin")
                    .HasColumnName("xmin")
                    .IsRowVersion();
            }
            // Database-level floor on stock counts. The service layer already clamps these, but a
            // CHECK is the last line of defence against a bug or raw SQL driving balances negative.
            // (We intentionally do NOT enforce Reserved <= OnHand: a POS sale draws against OnHand
            // without consulting online holds, so OnHand can legitimately dip below Reserved.)
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_StoreInventories_OnHand_NonNegative", "\"QuantityOnHand\" >= 0");
                t.HasCheckConstraint("CK_StoreInventories_Reserved_NonNegative", "\"QuantityReserved\" >= 0");
            });
        });

        // ─── Register (till) ────────────────────────────────────────────────
        builder.Entity<Register>(e =>
        {
            e.HasOne(r => r.Store).WithMany().HasForeignKey(r => r.StoreId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── TillSession (cashier shift) ────────────────────────────────────
        builder.Entity<TillSession>(e =>
        {
            e.HasIndex(s => new { s.RegisterId, s.ClosedAt });
            e.Property(s => s.OpeningFloat).HasPrecision(18, 2);
            e.Property(s => s.CountedCash).HasPrecision(18, 2);
            e.HasOne(s => s.Register).WithMany().HasForeignKey(s => s.RegisterId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.Orders).WithOne(o => o.TillSession).HasForeignKey(o => o.TillSessionId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ─── Refund / RefundItem ────────────────────────────────────────────
        builder.Entity<Refund>(e =>
        {
            e.Property(r => r.Amount).HasPrecision(18, 2);
            // RefundNumber is a human-facing document id and must be unique (like OrderNumber).
            e.HasIndex(r => r.RefundNumber).IsUnique();
            e.HasOne(r => r.OriginalOrder).WithMany().HasForeignKey(r => r.OriginalOrderId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Items).WithOne(i => i.Refund).HasForeignKey(i => i.RefundId)
             .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_Refunds_Amount_NonNegative", "\"Amount\" >= 0"));
        });
        builder.Entity<RefundItem>(e => e.Property(i => i.UnitPrice).HasPrecision(18, 2));

        // ─── CashMovement (pay-in / pay-out against a shift) ────────────────
        builder.Entity<CashMovement>(e =>
        {
            e.HasIndex(m => m.TillSessionId);
            e.Property(m => m.Amount).HasPrecision(18, 2);
            e.HasOne(m => m.TillSession).WithMany().HasForeignKey(m => m.TillSessionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── OrderPayment (split / mixed tenders on a POS sale) ─────────────
        builder.Entity<OrderPayment>(e =>
        {
            e.HasIndex(p => p.OrderId);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.HasOne(p => p.Order).WithMany().HasForeignKey(p => p.OrderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── StockTransfer ──────────────────────────────────────────────────
        builder.Entity<StockTransfer>(e =>
        {
            // Unique document id; Status/CreatedAt indexed for the transfers list + the
            // pending-count badge query that runs on every Inventory-area page load.
            e.HasIndex(t => t.TransferNumber).IsUnique();
            e.HasIndex(t => t.Status);
            e.HasIndex(t => t.CreatedAt);
            e.HasIndex(t => t.OrderId);   // find all transfers for an online order at finalisation
            e.HasOne(t => t.FromStore).WithMany().HasForeignKey(t => t.FromStoreId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.ToStore).WithMany().HasForeignKey(t => t.ToStoreId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(t => t.Items).WithOne(i => i.StockTransfer).HasForeignKey(i => i.StockTransferId).OnDelete(DeleteBehavior.Cascade);
        });

        // ─── StockTransferItem ──────────────────────────────────────────────
        builder.Entity<StockTransferItem>(e =>
        {
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_StockTransferItems_RequestedQty_Positive", "\"RequestedQty\" > 0");
                t.HasCheckConstraint("CK_StockTransferItems_StageQty_NonNegative",
                    "(\"ApprovedQty\" IS NULL OR \"ApprovedQty\" >= 0) AND (\"DispatchedQty\" IS NULL OR \"DispatchedQty\" >= 0) AND (\"ReceivedQty\" IS NULL OR \"ReceivedQty\" >= 0) AND (\"DamagedQty\" IS NULL OR \"DamagedQty\" >= 0) AND (\"WontFulfilQty\" IS NULL OR \"WontFulfilQty\" >= 0)");
            });
        });

        // ─── StockAdjustment (header for grouped adjustments, "BSA#####") ────
        builder.Entity<StockAdjustment>(e =>
        {
            e.HasIndex(a => a.AdjustmentNumber).IsUnique();
            e.HasIndex(a => a.CreatedAt);   // list ordering / date filter
            e.HasIndex(a => a.StoreId);     // branch filter
            e.Property(a => a.AdjustmentNumber).HasMaxLength(32);
            e.Property(a => a.Reason).HasMaxLength(64);
            e.Property(a => a.Source).HasMaxLength(16);
            e.HasOne(a => a.Store).WithMany().HasForeignKey(a => a.StoreId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Lines).WithOne(l => l.StockAdjustment).HasForeignKey(l => l.StockAdjustmentId).OnDelete(DeleteBehavior.Cascade);
        });

        // ─── StockAdjustmentLine ────────────────────────────────────────────
        builder.Entity<StockAdjustmentLine>(e =>
        {
            e.Property(l => l.UnitCost).HasColumnType("numeric");
            e.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.ProductVariant).WithMany().HasForeignKey(l => l.ProductVariantId).OnDelete(DeleteBehavior.SetNull);
        });

        // ─── StockMovement (stock ledger) ───────────────────────────────────
        builder.Entity<StockMovement>(e =>
        {
            e.HasIndex(m => new { m.ProductId, m.StoreId, m.CreatedAt });
            // RESTRICT: the ledger is permanent history — deleting a product must never wipe it
            // (deactivate the product instead). OP-7.
            e.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.ProductVariant).WithMany().HasForeignKey(m => m.ProductVariantId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.Store).WithMany().HasForeignKey(m => m.StoreId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ─── Order ──────────────────────────────────────────────────────────
        builder.Entity<Order>(e =>
        {
            e.HasIndex(o => o.OrderNumber).IsUnique();
            // Hot lookups: admin order list filters by Status; reports/listings sort by CreatedAt
            // and split by Channel; the payment webhook matches on PaymentReference.
            e.HasIndex(o => o.Status);
            e.HasIndex(o => o.CreatedAt);
            e.HasIndex(o => new { o.Channel, o.CreatedAt });
            e.HasIndex(o => o.PaymentReference).HasFilter("\"PaymentReference\" IS NOT NULL");
            // Idempotency for offline POS sales: one order per client-generated id (re-sync is a no-op).
            e.HasIndex(o => o.OfflineClientId).IsUnique().HasFilter("\"OfflineClientId\" IS NOT NULL");
            // Store-pickup QR token lookup (verify at the till) — unique, one per order.
            e.HasIndex(o => o.PickupToken).IsUnique().HasFilter("\"PickupToken\" IS NOT NULL");
            e.Property(o => o.Subtotal).HasPrecision(18, 2);
            e.Property(o => o.DeliveryFee).HasPrecision(18, 2);
            e.Property(o => o.Tax).HasPrecision(18, 2);
            e.Property(o => o.Total).HasPrecision(18, 2);
            e.Property(o => o.LoyaltyDiscount).HasPrecision(18, 2);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Orders_Subtotal_NonNegative", "\"Subtotal\" >= 0");
                t.HasCheckConstraint("CK_Orders_Total_NonNegative", "\"Total\" >= 0");
            });

            e.HasOne(o => o.PickupStore)
             .WithMany(s => s.Orders)
             .HasForeignKey(o => o.PickupStoreId)
             .IsRequired(false);

            // Branch that fulfilled a paid online order (no inverse nav; don't cascade).
            e.HasOne(o => o.FulfillingStore)
             .WithMany()
             .HasForeignKey(o => o.FulfillingStoreId)
             .OnDelete(DeleteBehavior.Restrict)
             .IsRequired(false);

            // POS buyer (distinct from User, which is the cashier on POS sales).
            e.HasOne(o => o.Customer)
             .WithMany()
             .HasForeignKey(o => o.CustomerUserId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);

            // RESTRICT: orders are financial history — deleting a user must not delete their orders
            // (was the EF convention default of Cascade). OP-7.
            e.HasOne(o => o.User)
             .WithMany(u => u.Orders)
             .HasForeignKey(o => o.UserId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ─── ParkedSale (held POS sales) ────────────────────────────────────
        builder.Entity<ParkedSale>(e =>
        {
            e.Property(p => p.Total).HasPrecision(18, 2);
            e.HasIndex(p => new { p.StoreId, p.CreatedAt });
        });

        // ─── NewsletterSubscriber ───────────────────────────────────────────
        builder.Entity<NewsletterSubscriber>(e => e.HasIndex(n => n.Email).IsUnique());

        // ─── UserStore (store-level authorization) ──────────────────────────
        builder.Entity<Models.Domain.UserStore>(e =>
        {
            e.HasIndex(us => new { us.UserId, us.StoreId }).IsUnique();
            e.HasOne(us => us.User).WithMany().HasForeignKey(us => us.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(us => us.Store).WithMany().HasForeignKey(us => us.StoreId).OnDelete(DeleteBehavior.Cascade);
        });

        // ─── StockReservation (soft holds for unpaid online orders) ─────────
        builder.Entity<StockReservation>(e =>
        {
            e.HasIndex(r => r.OrderId);
            // CreatedAt: scanned by the ReservationSweeper (CreatedAt < cutoff).
            // (ProductId, StoreId): joined when reserving/releasing holds per branch.
            e.HasIndex(r => r.CreatedAt);
            e.HasIndex(r => new { r.ProductId, r.StoreId });
            e.HasOne(r => r.Order).WithMany().HasForeignKey(r => r.OrderId)
             .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_StockReservations_Quantity_Positive", "\"Quantity\" > 0"));
        });

        // ─── OrderItem ──────────────────────────────────────────────────────
        builder.Entity<OrderItem>(e =>
        {
            e.Property(oi => oi.UnitPrice).HasPrecision(18, 2);
            e.Property(oi => oi.DiscountAmount).HasPrecision(18, 2);
            e.Ignore(oi => oi.LineTotal);
            // RESTRICT: order line items are sales history — deleting a product must not erase them
            // (was the EF convention default of Cascade). The order still owns its items (cascade
            // from Order is unchanged). OP-7.
            e.HasOne(oi => oi.Product).WithMany().HasForeignKey(oi => oi.ProductId)
             .OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_OrderItems_Quantity_Positive", "\"Quantity\" > 0");
                t.HasCheckConstraint("CK_OrderItems_UnitPrice_NonNegative", "\"UnitPrice\" >= 0");
                t.HasCheckConstraint("CK_OrderItems_Discount_NonNegative", "\"DiscountAmount\" >= 0");
            });
        });

        // ─── OrderNote ──────────────────────────────────────────────────────
        // Timeline notes belong to the order — cascade-delete with it; newest read first.
        builder.Entity<OrderNote>(e =>
        {
            e.HasOne(n => n.Order).WithMany(o => o.Timeline).HasForeignKey(n => n.OrderId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(n => n.OrderId);
        });

        // ─── AuditLog ───────────────────────────────────────────────────────
        // Filtered/sorted by Action, EntityType and CreatedAt in the admin audit list + CSV
        // export; this table grows unbounded so the indexes matter as it fills up.
        builder.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => a.CreatedAt);
            e.HasIndex(a => a.Action);
            e.HasIndex(a => a.EntityType);
        });

        // ─── PosDiscountReason / PosDiscountPreset ──────────────────────────
        builder.Entity<PosDiscountReason>(e =>
        {
            e.HasMany(r => r.Presets).WithOne(p => p.Reason)
             .HasForeignKey(p => p.ReasonId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PosDiscountPreset>(e =>
        {
            e.Property(p => p.Value).HasPrecision(18, 2);
        });

        // ─── WishlistItem ───────────────────────────────────────────────────
        builder.Entity<WishlistItem>(e =>
        {
            e.HasIndex(w => new { w.UserId, w.ProductId }).IsUnique();
        });

        // ─── StoreInventory.AvailableQuantity (computed, not mapped) ────────
        builder.Entity<StoreInventory>()
            .Ignore(si => si.AvailableQuantity);

        // ─── ProductAttribute ────────────────────────────────────────────────────
        builder.Entity<ProductAttribute>(e =>
        {
            e.HasIndex(a => a.Slug).IsUnique();
            e.HasMany(a => a.Values)
             .WithOne(v => v.Attribute)
             .HasForeignKey(v => v.AttributeId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── ProductVariant ──────────────────────────────────────────────────────
        builder.Entity<ProductVariant>(e =>
        {
            e.Property(v => v.PriceAdjustment).HasPrecision(18, 2);
            // Unique among real (non-blank) codes; Sku indexed for fast scan/lookup. Both columns
            // participate in the barcode-scan ScanLookup query.
            e.HasIndex(v => v.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL AND \"Barcode\" <> ''");
            e.HasIndex(v => v.Sku);
            e.HasMany(v => v.AttributeValues)
             .WithMany(av => av.Variants)
             .UsingEntity("ProductVariantAttributeValue");
        });

        // ─── Product computed properties ────────────────────────────────────
        builder.Entity<Product>()
            .Ignore(p => p.TotalStock)
            .Ignore(p => p.IsAvailable);

        // ─── SiteSetting ─────────────────────────────────────────────────────
        builder.Entity<SiteSetting>(e =>
        {
            e.HasIndex(s => s.Key).IsUnique();
        });

        // ─── RolePermission ──────────────────────────────────────────────────
        builder.Entity<RolePermission>(e =>
        {
            e.HasIndex(rp => new { rp.RoleName, rp.Section }).IsUnique();
        });

        // ─── DiscountCode ────────────────────────────────────────────────────
        builder.Entity<DiscountCode>(e =>
        {
            e.HasIndex(d => d.Code).IsUnique();
            e.Property(d => d.Value).HasPrecision(18, 2);
            e.Property(d => d.MinimumOrderAmount).HasPrecision(18, 2);

            e.HasMany(d => d.Categories)
             .WithOne(dc => dc.DiscountCode)
             .HasForeignKey(dc => dc.DiscountCodeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(d => d.Products)
             .WithOne(dp => dp.DiscountCode)
             .HasForeignKey(dp => dp.DiscountCodeId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── Order ───────────────────────────────────────────────────────────
        builder.Entity<Order>().Property(o => o.DiscountAmount).HasPrecision(18, 2);
    }
}
