using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface IStockService
{
    /// <summary>Current on-hand quantity for a product at a store (0 if no record).</summary>
    Task<int> GetStockAsync(int productId, int storeId);

    /// <summary>
    /// Applies a stock change: updates the running balance (StoreInventory) and appends a
    /// ledger entry (StockMovement). Does NOT call SaveChanges — the caller owns the transaction
    /// so multiple lines (and the related order) commit together. Returns the new balance.
    /// </summary>
    Task<int> ApplyAsync(int productId, int? variantId, int storeId, int quantityChange,
        StockMovementType type, string? reference = null, string? note = null, string? userId = null);
}

public class StockService : IStockService
{
    private readonly ApplicationDbContext _db;

    public StockService(ApplicationDbContext db) => _db = db;

    public async Task<int> GetStockAsync(int productId, int storeId)
    {
        var inv = await _db.StoreInventories
            .FirstOrDefaultAsync(si => si.ProductId == productId && si.StoreId == storeId);
        return inv?.QuantityOnHand ?? 0;
    }

    public async Task<int> ApplyAsync(int productId, int? variantId, int storeId, int quantityChange,
        StockMovementType type, string? reference = null, string? note = null, string? userId = null)
    {
        var now = DateTime.UtcNow;

        var inv = await _db.StoreInventories
            .FirstOrDefaultAsync(si => si.ProductId == productId && si.StoreId == storeId);
        if (inv == null)
        {
            inv = new StoreInventory { ProductId = productId, StoreId = storeId, QuantityOnHand = 0, LastSyncedAt = now };
            _db.StoreInventories.Add(inv);
        }

        inv.QuantityOnHand += quantityChange;
        inv.LastSyncedAt = now;

        _db.StockMovements.Add(new StockMovement
        {
            ProductId = productId,
            ProductVariantId = variantId,
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
