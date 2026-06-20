using System;
using System.Collections.Generic;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.ViewModels
{
    // ─── Dashboard ────────────────────────────────────────────────────────
    public class DashboardViewModel
    {
        public decimal RevenueToday { get; set; }
        public decimal RevenueThisMonth { get; set; }
        public int OrdersToday { get; set; }
        public int OrdersPending { get; set; }
        public int TotalProducts { get; set; }
        public int LowStockAlerts { get; set; }
        public int TotalCustomers { get; set; }
        public List<RecentOrderRow> RecentOrders { get; set; } = new();
        public List<LowStockRow> LowStockItems { get; set; } = new();
        public List<DailyRevenueRow> DailyRevenue { get; set; } = new();
        public List<TopProductRow> TopProducts { get; set; } = new();
        public int ChartDays { get; set; } = 30;
    }

    public class DailyRevenueRow
    {
        public string Date { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class RecentOrderRow
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public decimal Total { get; set; }
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public class LowStockRow
    {
        public string ProductName { get; set; } = "";
        public string StoreName { get; set; } = "";
        public int Quantity { get; set; }
    }

    public class TopProductRow
    {
        public string ProductName { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public int UnitsSold { get; set; }
        public decimal Revenue { get; set; }
    }

    // ─── Orders ───────────────────────────────────────────────────────────
    public class AdminOrderListViewModel
    {
        public List<AdminOrderRow> Orders { get; set; } = new();
        public string StatusFilter { get; set; } = "";
        public string SearchQuery { get; set; } = "";
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public Dictionary<string, int> StatusCounts { get; set; } = new();
    }

    public class AdminOrderRow
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        public decimal Total { get; set; }
        public string Status { get; set; } = "";
        public bool IsPaid { get; set; }
        public string FulfillmentType { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public class AdminOrderDetailViewModel
    {
        public Order Order { get; set; } = null!;
        public string CustomerName { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        // "Refunded" is intentionally excluded — refunds go through the dedicated Refund action
        // (records the refund, returns stock, triggers the gateway), not a status dropdown.
        public List<string> AvailableStatuses { get; set; } = new()
        {
            "Pending", "Confirmed", "Processing", "ReadyForPickup", "Shipped", "Delivered", "Cancelled"
        };

        // ── Refunds ──────────────────────────────────────────────────────────
        public List<Refund> Refunds { get; set; } = new();
        /// <summary>Quantity already refunded per (productId, variantId), to compute what's left.</summary>
        public Dictionary<(int ProductId, int? VariantId), int> RefundedQty { get; set; } = new();
        /// <summary>The store stock is returned to on refund (fulfilling store, else pickup store).</summary>
        public int RefundStoreId { get; set; }
        public bool CanRefund { get; set; }
        public decimal RefundedTotal { get; set; }

        /// <summary>Order timeline notes (system + staff), newest first.</summary>
        public List<OrderNote> Notes { get; set; } = new();

        // ── Customer history (across this customer's orders) ──
        public int CustomerTotalOrders { get; set; }
        public decimal CustomerTotalRevenue { get; set; }
        public decimal CustomerAvgOrderValue { get; set; }
    }

    // ─── Products ─────────────────────────────────────────────────────────
    public class AdminProductListViewModel
    {
        public List<Product> Products { get; set; } = new();
        public string SearchQuery { get; set; } = "";
        public string Sort { get; set; } = "name_asc";
        public string CategoryFilter { get; set; } = "";
        public string StatusFilter { get; set; } = "";
        public string TypeFilter { get; set; } = "";
        public decimal? MinPrice { get; set; }
        public decimal? MaxPrice { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public int TotalCount { get; set; }
        public List<Category> AvailableCategories { get; set; } = new();
    }

    public class AdminProductEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        // Optional fields are nullable so ASP.NET's implicit "non-nullable = required"
        // validation doesn't block saving when they're left blank.
        public string? Slug { get; set; }
        public string? Sku { get; set; }
        public string ProductType { get; set; } = "simple";
        public string? Description { get; set; }
        public string? ShortDescription { get; set; }
        public decimal Price { get; set; }
        public decimal? SalePrice { get; set; }
        public string? Colour { get; set; }
        public string? Weight { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsFeatured { get; set; }
        public bool IsNewArrival { get; set; }
        public string? ExternalCode { get; set; }
        public int? CategoryId { get; set; }
        public List<Category> Categories { get; set; } = new();
        public List<ProductImage> Images { get; set; } = new();

        // Variants tab
        public List<ProductAttribute> AllAttributes { get; set; } = new();
        public List<AdminVariantViewModel> Variants { get; set; } = new();

        // Selected attribute value IDs for variant generation (posted from form)
        public List<int> SelectedAttributeValueIds { get; set; } = new();
    }

    public class AdminVariantViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Sku { get; set; }
        public string? ImageUrl { get; set; }
        public decimal? PriceAdjustment { get; set; }
        public int StockQuantity { get; set; }
        public bool IsActive { get; set; } = true;
        public List<string> AttributeLabels { get; set; } = new();  // e.g. ["Gold", "18\""]
    }

    // ─── Attributes ───────────────────────────────────────────────────────
    public class AdminAttributeListViewModel
    {
        public List<ProductAttribute> Attributes { get; set; } = new();
    }

    public class AdminAttributeEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
        public List<ProductAttributeValue> Values { get; set; } = new();
        // Posted to add a new value
        public string NewValue { get; set; } = "";
    }

    // ─── Inventory ────────────────────────────────────────────────────────
    public class AdminInventoryViewModel
    {
        public List<Store> Stores { get; set; } = new();
        public List<ProductInventoryRow> Products { get; set; } = new();
        public DateTime? UpdatedAt { get; set; }
        public string SearchQuery { get; set; } = "";
        public string CategoryFilter { get; set; } = "";
        public string StockFilter { get; set; } = "";
        public List<Category> AvailableCategories { get; set; } = new();
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public int TotalCount { get; set; }
    }

    public class ProductInventoryRow
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public string? Sku { get; set; }
        public string CategoryName { get; set; } = "";
        public string? ImageUrl { get; set; }
        public int LowStockThreshold { get; set; } = 3;
        public Dictionary<int, int> StockByStore { get; set; } = new();   // storeId → qty (-1 = no record)

        /// <summary>Per-variant stock rows (empty for products without variants). When present, the
        /// product's own StockByStore is the unallocated "pool" and each variant tracks its own stock.</summary>
        public List<VariantInventoryRow> Variants { get; set; } = new();
        public bool HasVariants => Variants.Count > 0;

        // Only count stores that have an actual record (exclude -1); include variant rows.
        public int TotalStock        => StockByStore.Values.Where(v => v >= 0).Sum()
                                        + Variants.Sum(v => v.StockByStore.Values.Where(q => q >= 0).Sum());
        public bool HasAnyRecord     => StockByStore.Values.Any(v => v >= 0);
        public bool HasOutOfStock    => StockByStore.Values.Any(v => v == 0);
        public bool HasLowStock      => StockByStore.Values.Any(v => v > 0 && v < LowStockThreshold);
        public bool HasMissingRecord => StockByStore.Values.Any(v => v == -1);
    }

    public class VariantInventoryRow
    {
        public int VariantId { get; set; }
        public string Name { get; set; } = "";
        public string? Sku { get; set; }
        public Dictionary<int, int> StockByStore { get; set; } = new();   // storeId → qty (-1 = no record)
    }

    // ─── Customers ────────────────────────────────────────────────────────
    public class AdminCustomerListViewModel
    {
        public List<AdminCustomerRow> Customers { get; set; } = new();
        public string SearchQuery { get; set; } = "";
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
    }

    public class AdminCustomerRow
    {
        public string Id { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public int OrderCount { get; set; }
        public decimal TotalSpend { get; set; }
        public DateTime JoinedAt { get; set; }
        public DateTime? LastOrderAt { get; set; }
    }

    public class AdminCustomerDetailViewModel
    {
        public string Id { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public DateTime JoinedAt { get; set; }
        public int OrderCount { get; set; }
        public decimal TotalSpend { get; set; }
        public List<RecentOrderRow> RecentOrders { get; set; } = new();
    }

    // ─── Users ────────────────────────────────────────────────────────────
    public class AdminUserListViewModel
    {
        public List<AdminUserRow> Users { get; set; } = new();
        public string SearchQuery { get; set; } = "";
        public string RoleFilter { get; set; } = "";    // "" | <role name>
        public string StatusFilter { get; set; } = "";  // "" | active | locked
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public int TotalCount { get; set; }

        // All assignable roles (for the dropdown + role filter)
        public List<string> AvailableRoles { get; set; } = new();

        // Stat cards
        public int TotalUsers { get; set; }
        public int AdminCount { get; set; }
        public int CustomerCount { get; set; }
        public int LockedCount { get; set; }
        public int NewThisMonth { get; set; }
    }

    public class AdminUserRow
    {
        public string Id { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public string RoleName { get; set; } = "Customer";
        public bool IsAdmin { get; set; }
        public bool IsLocked { get; set; }
        public bool EmailConfirmed { get; set; }
        public int OrderCount { get; set; }
        public decimal TotalSpend { get; set; }
        public DateTime JoinedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
    }

    public class AdminCreateUserViewModel
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public string Password { get; set; } = "";
        public bool MakeAdmin { get; set; }
    }

    // ─── Roles & Permissions ───────────────────────────────────────────────
    public class AdminRoleListViewModel
    {
        public List<AdminRoleRow> Roles { get; set; } = new();
    }

    public class AdminRoleRow
    {
        public string Name { get; set; } = "";
        public bool IsSystem { get; set; }          // Admin / Customer — not editable/deletable
        public bool IsFullAccess { get; set; }      // Admin
        public int UserCount { get; set; }
        public List<string> Sections { get; set; } = new();  // section labels
    }

    public class AdminRoleEditViewModel
    {
        public string Name { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public bool IsNew { get; set; }
        public HashSet<string> SelectedSections { get; set; } = new();
    }

    // ─── Audit Log ────────────────────────────────────────────────────────
    public class AdminAuditLogViewModel
    {
        public List<AuditLogRow> Logs { get; set; } = new();
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public string ActionFilter { get; set; } = "";
        public string EntityFilter { get; set; } = "";
        public string DateFrom { get; set; } = "";
        public string DateTo { get; set; } = "";
        public string SearchQuery { get; set; } = "";
        public List<string> AvailableActions { get; set; } = new();
        public List<string> AvailableEntities { get; set; } = new();
    }

    public class AuditLogRow
    {
        public string Action { get; set; } = "";
        public string EntityType { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string Description { get; set; } = "";
        public string PerformedBy { get; set; } = "";
        public string? IpAddress { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    // ─── Discount Codes ───────────────────────────────────────────────────
    public class AdminDiscountListViewModel
    {
        public List<DiscountCode> DiscountCodes { get; set; } = new();
    }

    public class AdminDiscountEditViewModel
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string? Description { get; set; }
        public string Type { get; set; } = "Percentage";   // Percentage | FixedAmount | FreeShipping
        public decimal Value { get; set; }

        public string Scope { get; set; } = "EntireOrder";  // EntireOrder | Categories | Products
        public bool IsAutomatic { get; set; }

        public decimal? MinimumOrderAmount { get; set; }
        public int? MinimumQuantity { get; set; }
        public int? MaxUses { get; set; }
        public int? MaxUsesPerCustomer { get; set; }
        public bool FirstOrderOnly { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }

        // Scope selections
        public List<int> SelectedCategoryIds { get; set; } = new();
        public List<int> SelectedProductIds { get; set; } = new();

        // For rendering the pickers
        public List<Category> AllCategories { get; set; } = new();
        public List<Product> AllProducts { get; set; } = new();
    }

    public class AdminDiscountUsageViewModel
    {
        public DiscountCode Discount { get; set; } = null!;
        public List<DiscountUsageRow> Usages { get; set; } = new();
        public decimal TotalDiscounted { get; set; }
    }

    public class DiscountUsageRow
    {
        public string OrderNumber { get; set; } = "";
        public int OrderId { get; set; }
        public string CustomerName { get; set; } = "";
        public decimal DiscountAmount { get; set; }
        public decimal OrderTotal { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    // ─── Categories ───────────────────────────────────────────────────────
    public class AdminCategoryEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? Description { get; set; }
        public string? ImageUrl { get; set; }
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
        public int? ParentId { get; set; }
        public List<Category> AllCategories { get; set; } = new();
    }

    // ─── Stores ───────────────────────────────────────────────────────────
    public class AdminStoreEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Address { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? OpeningHours { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
