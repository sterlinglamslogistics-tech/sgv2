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
        public List<string> AvailableStatuses { get; set; } = new()
        {
            "Pending", "Confirmed", "Processing", "ReadyForPickup", "Shipped", "Delivered", "Cancelled", "Refunded"
        };
    }

    // ─── Products ─────────────────────────────────────────────────────────
    public class AdminProductListViewModel
    {
        public List<Product> Products { get; set; } = new();
        public string SearchQuery { get; set; } = "";
        public string CategoryFilter { get; set; } = "";
        public string StatusFilter { get; set; } = "";
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
        public string Slug { get; set; } = "";
        public string Description { get; set; } = "";
        public string? ShortDescription { get; set; }
        public decimal Price { get; set; }
        public string? Colour { get; set; }
        public string? Weight { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsFeatured { get; set; }
        public bool IsNewArrival { get; set; }
        public string ErpNextItemCode { get; set; } = string.Empty;
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
        public List<InventoryStoreSection> Stores { get; set; } = new();
        public DateTime? LastSyncedAt { get; set; }
    }

    public class InventoryStoreSection
    {
        public Store Store { get; set; } = null!;
        public List<InventoryProductRow> Products { get; set; } = new();
    }

    public class InventoryProductRow
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public string Sku { get; set; } = "";
        public int QuantityOnHand { get; set; }
        public int LowStockThreshold { get; set; } = 3;
        public bool IsLowStock => QuantityOnHand < LowStockThreshold;
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
        public string Type { get; set; } = "Percentage";
        public decimal Value { get; set; }
        public decimal? MinimumOrderAmount { get; set; }
        public int? MaxUses { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    // ─── Categories ───────────────────────────────────────────────────────
    public class AdminCategoryEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? Description { get; set; }
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
        public string ErpNextWarehouse { get; set; } = "";
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
