# Audit Fix Tracker

Living checklist of every fix and recommendation from the ongoing audit. We add items as we
audit, then work the **Open** list top-to-bottom. Companion to `docs/AUDIT_REPORT.md` (the
original findings narrative) — IDs like `C1`/`H6` refer to that report.

**Last updated:** 2026-06-14 (security hardening → FX-33 CSP, FX-34 staff session; OP-45 added)

**Legend:** severity 🔴 Critical · 🟠 High · 🟡 Medium · 🟢 Low ·
status ✅ done · 🔲 open · ⏳ in progress · ⛔ blocked

---

## ✅ Fixed — committed (`27a22b8` "storefront add-to-bag")

| ID | Item | Location |
|----|------|----------|
| FX-1 | Variant dropdown `onAttrSelectChange` exposed on `window` (was undefined → "select all options" blocked add) | Views/Products/Detail.cshtml |
| FX-2 | `@Html.AntiForgeryToken()` on product page (CartController `[ValidateAntiForgeryToken]` was 400-ing every add) | Views/Products/Detail.cshtml |
| FX-3 | Add-to-cart error toast on failure (was silent) | Views/Products/Detail.cshtml, wwwroot/js/app.js |
| FX-4 | Cart badge now created on first add + reads live session count (appears/increments/persists) | wwwroot/js/app.js, Views/Shared/_Navigation.cshtml |

---

## ✅ Fixed — uncommitted (in working tree, needs commit)

### Phase A/B security + concurrency (prior session)
| ID | Item | Ref | Location |
|----|------|-----|----------|
| FX-5 | Path-traversal guard on admin upload (rejects `..`, Guid filename, GetFullPath) | C2 | Areas/Admin/Controllers/UploadController.cs |
| FX-6 | `StoreInventory` optimistic concurrency token (`xmin` rowversion) | C5 | Data/ApplicationDbContext.cs (+migration; deploy-blocker OP-3 now fixed) |
| FX-7 | Auth cookie `SecurePolicy=Always` outside Development | C6 | Program.cs |
| FX-8 | Security headers (X-Content-Type-Options, X-Frame-Options, Referrer-Policy) | — | Program.cs |
| FX-9 | CartController actions `[ValidateAntiForgeryToken]` | — | Controllers/CartController.cs |
| FX-10 | POS checkout race closed (FOR UPDATE + re-check in tx) | C3 | Controllers/TillController.cs |
| FX-11 | Online reservation race closed (row locks) | C4 | Services/OrderFulfilmentService.cs |
| FX-12 | Order money columns `HasPrecision(18,2)` | H1 | Data/ApplicationDbContext.cs |
| FX-13 | Transfer approval re-checks availability under lock | H3 | Services/TransferWorkflowService.cs |
| FX-14 | Transfer dispatch reloads inventory under lock | H4 | Services/TransferWorkflowService.cs |
| FX-15 | `ReleaseReservationAsync` wrapped in its own transaction | H5 | Services/OrderFulfilmentService.cs |
| FX-16 | Refund double-refund race closed (order row lock) | H6 | Controllers/TillController.cs |
| FX-17 | Negative-stock clamp (`InsufficientStockException`) on all ledger writes | — | Services/StockService.cs |

### This session (inventory + DB)
| ID | Item | Location |
|----|------|----------|
| FX-18 | Scan box empty-Enter no longer reloads (preserves unsaved grid edits) | Areas/Inventory/Views/Stock/Index.cshtml, Stocktake/Index.cshtml |
| FX-19 | DB integrity migration: 13 indexes + 4 unique (RefundNumber, TransferNumber, Product/Variant Barcode) + 12 CHECK constraints | Migrations/…_DatabaseIntegrityHardening.cs (applied to dev ✅) |
| FX-22 | Neutralized `StoreInventoryConcurrencyToken` migration `Up()`/`Down()` to no-ops (`AddColumn("xmin")` conflicts with the Postgres system column) — unblocks FX-6 & FX-19 deploy | Migrations/…_StoreInventoryConcurrencyToken.cs |
| FX-23 | Admin image upload was gated on a phantom "Upload" section (not in `AdminSections.All`) → only full Admins could upload; re-gated on its sole caller's section ("Settings") | Areas/Admin/Controllers/UploadController.cs |
| FX-24 | POS discount reason/preset CRUD (5 mutations) had **no audit trail** — added `LogAsync` to all five | Areas/Admin/Controllers/PosController.cs |
| FX-25 | Customer order detail never showed the `TrackingNumber` (admin captured it, customer couldn't see it — "shipped, on its way" with no number); now surfaced in the Fulfillment card | Views/Account/OrderDetail.cshtml |
| FX-26 | Merchandising: new `IMerchandisingService` (best sellers / trending / new arrivals / recently-viewed) + cookie-based recently-viewed; **Best Sellers, Trending Now, Recently Viewed** rows added to the home page (reusable `_ProductCardRow` partial). No migration. | Services/MerchandisingService.cs, Infrastructure/RecentlyViewed.cs, Controllers/HomeController.cs + ProductsController.cs, Views/Shared/_ProductCardRow.cshtml, Views/Home/Index.cshtml |
| FX-27 | SEO: added **WebSite + SearchAction** JSON-LD sitewide (enables Google sitelinks search box) | Views/Shared/_Layout.cshtml |
| FX-28 | SEO: **image sitemap** — sitemap now emits `<image:image>` for every product (993 images exposed to image search) | Controllers/SeoController.cs |
| FX-29 | Perf: product listing query → **SQL projection** (dropped 4 `Include`s that caused a cartesian JOIN blow-up + over-fetch on the busiest page); TTFB ~20–40ms with ~1k products | Controllers/ProductsController.cs |
| FX-30 | Perf (LCP): hero image now `loading="eager" fetchpriority="high" decoding="async"` so the largest paint isn't deprioritized | Views/Home/Index.cshtml |
| FX-31 | **Stored XSS** in admin delete-confirm handlers — product/store name was interpolated into inline JS with only `'`-escaping; crafted/imported names could break out and run in an Admin session. Moved name to a Razor-encoded `data-itemname` read as a string arg. Verified a payload name no longer executes. | Areas/Admin/Views/Products/Index.cshtml, Stores/Index.cshtml, Stores/Edit.cshtml |
| FX-32 | **Anonymous order PII leak (OP-44)** — `Checkout/Confirmation?orderNumber=` returned any order's name/address/phone to unauthenticated visitors. Now requires the signed-in owner **or** a Data-Protection-signed token (issued on the post-payment redirect). Verified: anon no/bogus token → 404; owner → 200. | Controllers/CheckoutController.cs |
| FX-33 | **CSP header (OP-10)** — added a Content-Security-Policy (default-src 'self', external scripts/framing/base/form blocked; Google Fonts + https images allowed). Verified: no CSP violations on home/detail; inline handlers still run. ('unsafe-inline' remains until inline scripts move to nonces — see OP-45.) | Program.cs |
| FX-34 | **Staff session lifetime (H9)** — staff/admin now get an 8-hour, non-persistent auth cookie (overrides "remember me"); shoppers keep the 30-day sliding cookie. Verified: staff+RememberMe → session cookie. | Program.cs |
| FX-20 | Typed stock movements — adjustment reasons map to `Purchase`/`Damage`/`Loss` (was all `Adjustment`) | Models/Domain/StockMovement.cs, Areas/Inventory/Controllers/StockController.cs |
| FX-21 | Concurrency safety on manual adjustments + stock-take (lock + re-read + graceful catch) | Areas/Inventory/Controllers/StockController.cs, StocktakeController.cs |

---

## 🔲 Open — work these top-to-bottom

### 🔴 Critical
| ID | Item | Ref | Notes / approach |
|----|------|-----|------------------|
| OP-1 | Paystack **test** keys exposed in **git history** (commits before `4be47d9` "stop tracking dev secrets file"). Source is already clean: base `appsettings.json` = `YOUR_…` placeholders; dev file gitignored/untracked. **Remaining = USER ACTION:** rotate the test keys in the Paystack dashboard (history scrub then unnecessary since test-only). No code change. | C1 | ⚠️ user-only |
| OP-2 | `FulfilPaidOrderAsync` swallows **all** exceptions → customer charged but order silently unfulfilled, no retry/alert | R2 / #12 | Outbox or job queue + retry + alert; mark order needs-attention. |
| ~~OP-3~~ | ✅ **DONE** (FX-22) — `StoreInventoryConcurrencyToken` `Up()`/`Down()` neutralized to no-ops; both pending migrations applied to dev & verified (xmin still a system column, 12 CHECKs + indexes live) | D1 | — |

### 🟠 High
| ID | Item | Ref | Notes / approach |
|----|------|-----|------------------|
| ~~OP-44~~ | ✅ **DONE** (FX-32) — anonymous order-confirmation PII leak closed via Data-Protection-signed token (owner-or-token) | security audit | — |
| OP-4 | Variant-level stock not tracked — `StoreInventory` is product-level only → variant availability is fiction, oversell risk | R1 | Add variant dimension to inventory + ledger; larger change. |
| OP-5 | POS sells against `OnHand` ignoring `QuantityReserved` → can drop `OnHand` below `Reserved`, leaving an online order short at fulfilment | #11 / I1 | POS sell against *available*, or accept + warn on low-available. |
| OP-6 | Payment webhook matches order by `reference.Contains(OrderNumber)` (substring) | R3 | Match exact `PaymentReference` / metadata `order_id`. |
| OP-7 | FK delete cascades destroy history: product delete → `OrderItems`+`StockMovements`; user delete → `Orders` | D2 | Change those FKs CASCADE→RESTRICT (behavior-affecting migration; soft-delete already exists). |
| OP-8 | No store-level authorization — any Inventory user can act on any branch | H7 | Per-branch scoping on stock/transfers/till. |
| OP-9 | `_ValidationScriptsPartial` references `wwwroot/lib/jquery-validation*` which is empty → 404, client validation degraded | R11 | Restore libs or drop the partial; quick win. |
| OP-33 | **Back-in-stock notify is a no-op** — `ProductsController.NotifyRestock` only `LogInformation`s and discards the email; customers told "we'll notify you" but nothing is stored/sent (revenue leak + broken promise) | merch audit | Persist requests (table) + send on restock (hook in `StockService.ApplyAsync` when qty crosses 0→+). |
| OP-28 | No online-order refund workflow — refunds are POS-only; `OrdersController.UpdateStatus` lets an order be set "Refunded" with **no Refund record, no stock return, no gateway refund** (cosmetic) | admin audit | Build online refund (record + `Return` ledger + provider refund). |
| OP-31 | Guests can't view order history/tracking after leaving the confirmation page (guest account has a random password → can't log in) | storefront audit | Magic-link order lookup, or post-purchase set-password. Ties to OP-11/R4. |

### 🟡 Medium
| ID | Item | Ref | Notes |
|----|------|-----|-------|
| ~~OP-10~~ | ✅ **DONE** (FX-33) — CSP header added (pragmatic, `'unsafe-inline'` for now) | R7 | — |
| OP-45 | CSP still needs `'unsafe-inline'` for scripts — refactor inline `<script>`/`onsubmit`/`onchange` to nonces/external handlers to drop it (full XSS hardening) | follow-up to FX-33 | Nonce middleware + move inline handlers. |
| OP-11 | Guest checkout creates unverified `ApplicationUser` keyed by email (account sprawl / cross-person history) | R4 | True guest order or email verification + merge. |
| OP-12 | Auto-`MigrateAsync()` on production startup (bad migration → site down) | R5 | Run migrations as a gated deploy step. |
| OP-13 | Only Paystack has a webhook; Stripe/Flutterwave rely on browser callback only | R6 | Add webhooks if those providers go live. |
| OP-14 | Dev `EnsureCreated` vs Prod `Migrate` — migrations never exercised in dev | R8 | Use migrations in dev too. |
| OP-15 | Stock ledger sparse — opening balances imported w/o `StockMovement` rows (can't reconstruct stock) | D3 / #15 | Write opening-stock `Adjustment` movements on import. |
| OP-16 | No dedicated Purchase/PO module (stock receipt is a typed adjustment) + no shrinkage report | I3 | Build POs; report grouping new `Damage`/`Loss` types. |
| OP-17 | Reports do in-memory aggregation over all products×stores; audit export unbounded; bulk/stocktake loop a query per line; customers N+1 | H10–H14 | Push aggregation to SQL; paginate/limit. (Audit filter + list partly helped by new indexes.) |
| OP-18 | Weak password policy + no email confirmation (H8). ~~30-day staff cookie (H9)~~ ✅ done (FX-34). | H8, H9 | Stronger password policy / email confirmation still open. |
| OP-19 | Thin automated test coverage (no POS/transfer/discount/payment/authz tests) | R9 | Add integration tests. |
| OP-27 | No admin view for **Reservations** (stock holds on unpaid orders) — active holds are invisible, so "out of stock" caused by holds can't be diagnosed | admin audit | Read-only holds list (order, product, branch, qty, age). |

### 🟢 Low
| ID | Item | Ref | Notes |
|----|------|-----|-------|
| OP-20 | Refund skips restock when `storeId==0` (practically unreachable) | I4 | Fall back to register store / validate. |
| OP-21 | Refunds & POS sales not in `AuditLog` (have own records) | I5 | Optional, for uniform audit. |
| OP-22 | Audit log doesn't capture old/new values | H15 | Add before/after snapshot. |
| OP-23 | `ParkedSale` stored as opaque, unversioned `CartJson` | R12 | Versioned schema. |
| OP-24 | Session/cache default in-memory (no horizontal scale unless Redis set) | R13 | Configure Redis for prod. |
| OP-42 | No output/response caching on anonymous pages (home/listing/detail) — every hit re-queries | perf audit | Add `OutputCache` w/ short TTL; donut/vary for the personalized nav (cart badge, wishlist). |
| OP-43 | No CDN / long-cache headers on static assets + product images served by the app | perf audit / CDN readiness | Front static + `/uploads` with a CDN; set `Cache-Control: immutable` on hashed assets. |
| OP-25 | Build warning CS0108: `TransfersController.Request` hides `ControllerBase.Request` | — | Rename or `new`. |
| OP-26 | Storefront: no reviews/ratings, no abandoned-cart recovery, product images lack width/height (CLS) | H16–H18 | Conversion/SEO backlog. |
| OP-29 | Data-export actions not all audited (Orders `ExportCsv` is a GET, unlogged; Customers/AuditLog exports *are* logged) | admin audit | Add `LogAsync` to remaining export endpoints. |
| OP-41 | `wwwroot/js/app.js` (8 KB) ships unminified | perf audit | Minify in the build/asset pipeline. |
| OP-30 | Store `OpeningHours` renders mojibake (`Monâ€"Sat: 8amâ€"8pm`) on order detail + Stores page — UTF-8 en-dash double-encoded in the stored value | storefront audit | Re-save store hours with clean dashes (data fix) or sanitize on display. |
| OP-32 | Product detail "Reviews (0)" tab is a dead end — always "no reviews", no way to submit (no reviews feature) | storefront audit / H16 | Build reviews, or hide the tab until shipped. |
| OP-38 | Product JSON-LD lacks `priceValidUntil` / `shippingDetails` / `hasMerchantReturnPolicy` (Google Merchant rich-result enrichments) | SEO audit | Add to Detail.cshtml product schema. |
| OP-39 | BreadcrumbList JSON-LD only on product detail, not on category/listing pages | SEO audit | Add breadcrumb schema to Products/Index. |
| OP-40 | No `aggregateRating`/`review` schema (blocked on reviews feature) — do **not** fake it | SEO audit / OP-32 | Add once reviews ship. |
| OP-34 | **Frequently Bought Together** not implemented | merch audit | Co-purchase analysis (OrderItems self-join by OrderId); cache nightly; show on detail. |
| OP-35 | **Save for Later** (move cart item → saved) not implemented (wishlist is adjacent) | merch audit | Add saved-items list to cart state; "Save for later"/"Move to bag" actions. |
| OP-36 | **Abandoned-cart recovery** not implemented (no cart capture/email) | merch audit / H17 | Persist cart w/ email on checkout-start; background job emails after N hrs; recovery link. |
| OP-37 | **Loyalty foundation** not implemented | merch audit | `LoyaltyAccount`/`PointsLedger` tables; accrue on paid order; redeem at checkout. |

> Full Medium/Low long-tail is in `docs/AUDIT_REPORT.md` (Medium Findings, Low Findings, Reports Gap Analysis).
