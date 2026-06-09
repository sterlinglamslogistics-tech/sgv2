using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

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
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundItem> RefundItems => Set<RefundItem>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferItem> StockTransferItems => Set<StockTransferItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<DiscountCode> DiscountCodes => Set<DiscountCode>();
    public DbSet<DiscountCategory> DiscountCategories => Set<DiscountCategory>();
    public DbSet<DiscountProduct> DiscountProducts => Set<DiscountProduct>();
    public DbSet<PosDiscountReason> PosDiscountReasons => Set<PosDiscountReason>();
    public DbSet<PosDiscountPreset> PosDiscountPresets => Set<PosDiscountPreset>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ─── Product ────────────────────────────────────────────────────────
        builder.Entity<Product>(e =>
        {
            e.HasIndex(p => p.Slug).IsUnique();
            // Unique only for real codes — products created without an ERPNext code
            // store "" and must not collide with each other.
            e.HasIndex(p => p.ErpNextItemCode).IsUnique().HasFilter("\"ErpNextItemCode\" <> ''");
            e.Property(p => p.Price).HasPrecision(18, 2);

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
            // Only enforce uniqueness on non-empty warehouse codes
            e.HasIndex(s => s.ErpNextWarehouse)
             .IsUnique()
             .HasFilter("\"ErpNextWarehouse\" <> ''");
        });

        // ─── StoreInventory ─────────────────────────────────────────────────
        builder.Entity<StoreInventory>(e =>
        {
            e.HasIndex(si => new { si.ProductId, si.StoreId }).IsUnique();
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
            e.HasOne(r => r.OriginalOrder).WithMany().HasForeignKey(r => r.OriginalOrderId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Items).WithOne(i => i.Refund).HasForeignKey(i => i.RefundId)
             .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<RefundItem>(e => e.Property(i => i.UnitPrice).HasPrecision(18, 2));

        // ─── StockTransfer ──────────────────────────────────────────────────
        builder.Entity<StockTransfer>(e =>
        {
            e.HasOne(t => t.FromStore).WithMany().HasForeignKey(t => t.FromStoreId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.ToStore).WithMany().HasForeignKey(t => t.ToStoreId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(t => t.Items).WithOne(i => i.StockTransfer).HasForeignKey(i => i.StockTransferId).OnDelete(DeleteBehavior.Cascade);
        });

        // ─── StockMovement (stock ledger) ───────────────────────────────────
        builder.Entity<StockMovement>(e =>
        {
            e.HasIndex(m => new { m.ProductId, m.StoreId, m.CreatedAt });
            e.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.ProductVariant).WithMany().HasForeignKey(m => m.ProductVariantId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.Store).WithMany().HasForeignKey(m => m.StoreId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ─── Order ──────────────────────────────────────────────────────────
        builder.Entity<Order>(e =>
        {
            e.HasIndex(o => o.OrderNumber).IsUnique();
            e.Property(o => o.Subtotal).HasPrecision(18, 2);
            e.Property(o => o.DeliveryFee).HasPrecision(18, 2);
            e.Property(o => o.Tax).HasPrecision(18, 2);
            e.Property(o => o.Total).HasPrecision(18, 2);

            e.HasOne(o => o.PickupStore)
             .WithMany(s => s.Orders)
             .HasForeignKey(o => o.PickupStoreId)
             .IsRequired(false);

            // POS buyer (distinct from User, which is the cashier on POS sales).
            e.HasOne(o => o.Customer)
             .WithMany()
             .HasForeignKey(o => o.CustomerUserId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);
        });

        // ─── ParkedSale (held POS sales) ────────────────────────────────────
        builder.Entity<ParkedSale>(e =>
        {
            e.Property(p => p.Total).HasPrecision(18, 2);
            e.HasIndex(p => new { p.StoreId, p.CreatedAt });
        });

        // ─── OrderItem ──────────────────────────────────────────────────────
        builder.Entity<OrderItem>(e =>
        {
            e.Property(oi => oi.UnitPrice).HasPrecision(18, 2);
            e.Property(oi => oi.DiscountAmount).HasPrecision(18, 2);
            e.Ignore(oi => oi.LineTotal);
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
