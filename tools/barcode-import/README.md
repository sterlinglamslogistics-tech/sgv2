# Barcode import (EposNow)

Imports the barcodes used on EposNow into our catalogue, matched by SKU.

## Source
`eposnow_barcodes.csv` — `sku,color,barcode`, extracted from the EposNow stock export
(`New Stocks …xlsx`). Each row is one physical variant: the **SKU-CODE = our `Product.Sku`**,
the **Barcode** is that variant's label, and **color** (Gold/Silver/Rosegold) is filled where
the export had it.

## Run
```
dotnet run -- import-barcodes "tools/barcode-import/eposnow_barcodes.csv"
```

For each SKU that matches a product it sets the product's primary barcode and assigns the
per-variant barcodes — **colour-matched first** (Gold barcode → Gold variant), then filling
any remaining variant slots. Because our scan resolves any barcode to its parent product,
every assigned barcode becomes scannable across the Inventory System and the till.
Re-runnable (overwrites). Implemented by `Services/BarcodeImportService.cs`.

## Coverage (current file)
612 rows / 276 SKUs → **239 products matched** (87%), 239 product + 425 variant barcodes
assigned; 37 SKUs are EposNow products not in our catalogue (skipped); 17 barcodes had no
free variant slot.
