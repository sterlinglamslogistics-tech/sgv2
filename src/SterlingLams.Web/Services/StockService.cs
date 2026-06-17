using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>Thrown by <see cref="IStockService.ApplyAsync"/> when a stock change would
/// drive QuantityOnHand negative. Callers should catch this, roll back the transaction,
/// and surface a friendly "not enough stock" message.</summary>
public class InsufficientStockException : Exception
{
    public int ProductId { get; }
    public int StoreId { get; }
    public int Available { get; }
    public int Requested { get; }

    public InsufficientStockException(int productId, int storeId, int available, int quantityChange)
        : base($"Insufficient stock for product {productId} at store {storeId}: have {available}, requested {-quantityChange}.")
    {
        ProductId = productId;
        StoreId = storeId;
        Available = available;
        Requested = -quantityChange;
    }
}

public interface IStockService
{
    /// <summary>
    /// On-hand quantity for a (product, variant) at a store (0 if no record).
    /// <paramref name="fallback"/> (default true) implements the transition rule: if the variant
    /// has its own balance row, use it; otherwise fall back to the product-level pool row. Pass
    /// false to read the EXACT variant row only (used when setting per-variant stock so the delta
    /// is measured against the variant row, not the shared pool). variantId null = the pool row.
    /// </summary>
    Task<int> GetStockAsync(int productId, int? variantId, int storeId, bool fallback = true);

    /// <summary>Available quantity (on-hand − reserved) for a (product, variant) at a store, using
    /// the same fallback rule as <see cref="GetStockAsync"/>.</summary>
    Task<int> GetAvailableAsync(int productId, int? variantId, int storeId);

    /// <summary>
    /// Applies a stock change: updates the running balance (StoreInventory) and appends a
    /// ledger entry (StockMovement). Does NOT call SaveChanges — the caller owns the transaction
    /// so multiple lines (and the related order) commit together. Returns the new balance.
    ///
    /// Row resolution: variantId null → the product pool row. variantId set with
    /// <paramref name="materializeVariant"/>=false (sales/transfers/online) → the variant's row if
    /// it exists, else the pool row (fallback). materializeVariant=true (setting per-variant stock
    /// from the grid/stock-take) → the variant's own row, created if missing.
    /// </summary>
    Task<int> ApplyAsync(int productId, int? variantId, int storeId, int quantityChange,
        StockMovementType type, string? reference = null, string? note = null, string? userId = null,
        bool materializeVariant = false);
}

public class StockService : IStockService
{
    private readonly ApplicationDbContext _db;

    public StockService(ApplicationDbContext db) => _db = db;

    // Picks which StoreInventory row a (product, variant, store) read/write resolves to.
    // variantId null  → the product pool (simple products).
    // variantId set   → ALWAYS the variant's own row — variants never fall back to the product
    //                   pool, so an out-of-stock variant reads 0 and cannot borrow shared stock.
    // (The exactVariant/noTracking params are kept for the call sites but no longer affect the
    //  resolution now that the pool-fallback transition is complete — stock lives on the variants.)
    private Task<int?> ResolveTargetVariantIdAsync(int productId, int? variantId, int storeId,
        bool exactVariant, bool noTracking)
        => Task.FromResult(variantId);

    public async Task<int> GetStockAsync(int productId, int? variantId, int storeId, bool fallback = true)
    {
        // AsNoTracking: callers often check stock both before and after acquiring a row lock
        // (e.g. TillController.Checkout). A tracking read here would seed the change tracker with
        // pre-lock values, so EF's identity map would hand the same stale instance (and stale xmin)
        // back to the later ApplyAsync, causing a spurious DbUpdateConcurrencyException on save.
        var targetVid = await ResolveTargetVariantIdAsync(productId, variantId, storeId,
            exactVariant: !fallback, noTracking: true);
        var inv = await _db.StoreInventories.AsNoTracking()
            .FirstOrDefaultAsync(si => si.ProductId == productId && si.StoreId == storeId
                && si.ProductVariantId == targetVid);
        return inv?.QuantityOnHand ?? 0;
    }

    public async Task<int> GetAvailableAsync(int productId, int? variantId, int storeId)
    {
        var targetVid = await ResolveTargetVariantIdAsync(productId, variantId, storeId,
            exactVariant: false, noTracking: true);
        var inv = await _db.StoreInventories.AsNoTracking()
            .FirstOrDefaultAsync(si => si.ProductId == productId && si.StoreId == storeId
                && si.ProductVariantId == targetVid);
        return inv == null ? 0 : Math.Max(0, inv.QuantityOnHand - inv.QuantityReserved);
    }

    public async Task<int> ApplyAsync(int productId, int? variantId, int storeId, int quantityChange,
        StockMovementType type, string? reference = null, string? note = null, string? userId = null,
        bool materializeVariant = false)
    {
        var now = DateTime.UtcNow;

        var targetVid = await ResolveTargetVariantIdAsync(productId, variantId, storeId,
            exactVariant: materializeVariant, noTracking: false);

        var inv = await _db.StoreInventories
            .FirstOrDefaultAsync(si => si.ProductId == productId && si.StoreId == storeId
                && si.ProductVariantId == targetVid);
        if (inv == null)
        {
            inv = new StoreInventory
            {
                ProductId = productId,
                ProductVariantId = targetVid,
                StoreId = storeId,
                QuantityOnHand = 0,
                UpdatedAt = now
            };
            _db.StoreInventories.Add(inv);
        }

        var newQty = inv.QuantityOnHand + quantityChange;
        if (newQty < 0)
            throw new InsufficientStockException(productId, storeId, inv.QuantityOnHand, quantityChange);

        inv.QuantityOnHand = newQty;
        inv.UpdatedAt = now;

        _db.StockMovements.Add(new StockMovement
        {
            ProductId = productId,
            ProductVariantId = variantId,   // ledger always records the actual variant sold/moved
            StoreId = storeId,
            QuantityChange = quantityChange,
            BalanceAfter = inv.QuantityOnHand,
            Type = type,
            Reference = reference,
            Note = note,
            CreatedByUserId = userId,
            CreatedAt = now
        });

        return inv.QuantityOnHand;
    }
}
