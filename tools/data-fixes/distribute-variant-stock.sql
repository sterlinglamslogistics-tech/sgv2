-- Move stock from the product "pool" onto variants, for every variant product.
--
-- Adopts the standard model: variant products hold stock on their VARIANTS (the product figure is
-- just the roll-up); simple products keep their single figure. Pairs with the StockService change
-- that removes pool-fallback for variants (an out-of-stock variant can't borrow the pool).
--
-- Idempotent-ish: assumes variant products currently have only pool rows (no per-variant rows yet).
-- Distributes each product's pool (per store) across its active variants, remainder to lowest ids,
-- then zeroes the pool. Already applied to dev + live 2026-06-17.

BEGIN;
WITH vars AS (
    SELECT v."Id" AS variant_id, v."ProductId",
           ROW_NUMBER() OVER (PARTITION BY v."ProductId" ORDER BY v."Id") AS rn,
           COUNT(*)     OVER (PARTITION BY v."ProductId")                  AS n
    FROM "ProductVariants" v WHERE v."IsActive"
),
pool AS (
    SELECT si."ProductId", si."StoreId", si."QuantityOnHand" AS pool_qty
    FROM "StoreInventories" si
    WHERE si."ProductVariantId" IS NULL
      AND si."ProductId" IN (SELECT DISTINCT "ProductId" FROM vars)
)
INSERT INTO "StoreInventories" ("ProductId","ProductVariantId","StoreId","QuantityOnHand","QuantityReserved","UpdatedAt")
SELECT vars."ProductId", vars.variant_id, pool."StoreId",
       (pool.pool_qty / vars.n) + CASE WHEN vars.rn <= (pool.pool_qty % vars.n) THEN 1 ELSE 0 END,
       0, NOW()
FROM vars JOIN pool ON pool."ProductId" = vars."ProductId";

UPDATE "StoreInventories" si
SET "QuantityOnHand" = 0, "UpdatedAt" = NOW()
WHERE si."ProductVariantId" IS NULL
  AND si."ProductId" IN (SELECT DISTINCT "ProductId" FROM "ProductVariants" WHERE "IsActive");
COMMIT;
