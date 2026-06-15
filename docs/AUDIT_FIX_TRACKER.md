# Audit Fix Tracker

Living checklist of every fix and recommendation from the ongoing audit. We add items as we
audit, then work the **Open** list top-to-bottom. Companion to `docs/AUDIT_REPORT.md` (the
original findings narrative) — IDs like `C1`/`H6` refer to that report.

**Last updated:** 2026-06-15 (FX-52 Save for Later — OP-35 done)

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
| FX-37 | **Variant-level stock — Phase 2 (OP-4)**: storefront product-detail per-variant availability + add-to-bag block for out-of-stock variant; cart `CombinedAvailableAsync` per-variant; **online reservation + fulfilment keyed by (store, product, variant)** via a shared effective-row resolver (variant row if stocked, else pool) so shared pools can't oversell. Verified e2e: a DevConfirm order for a stocked variant deducted the variant row (5→4), ledger Sale tagged the variant; detail availability 12/0 (stocked/fallback-to-zeroed-pool). | Services/OrderFulfilmentService.cs, Controllers/CartController.cs + ProductsController.cs, Models/ViewModels/ProductDetailViewModel.cs, Views/Products/Detail.cshtml |
| FX-36 | **Variant-level stock — Phase 1 (OP-4)**: `StoreInventory`/`StockReservation` gain nullable `ProductVariantId` (partial unique indexes); `StockService` resolves the variant row with a pool **fallback** (+`materializeVariant` for explicit per-variant sets, +`GetAvailableAsync`); Inventory Stock grid shows editable **per-variant rows**; POS checkout checks stock per variant. Online fulfilment/transfers pool-scoped (Phase 2). Verified: setting a variant's stock creates a distinct row, pool untouched. | Models/Domain/StoreInventory.cs + StockReservation.cs, Data/ApplicationDbContext.cs (+migration VariantLevelStock), Services/StockService.cs + OrderFulfilmentService.cs + TransferWorkflowService.cs, Areas/Inventory/Controllers/{Stock,Stocktake,Reports}Controller.cs + Views/Stock/Index.cshtml, Controllers/TillController.cs, Areas/Admin/{Controllers/InventoryController.cs,ViewModels/AdminViewModels.cs} |
| FX-35 | **Store-level authorization (OP-8 / H7)** — new `UserStore` join table + `IStoreAccessService` (Admin→all; assigned→those; none→all/legacy). Writes-only enforcement on stock edits, stock-take, transfers (approve/dispatch=from, receive=to, etc.) and till (open/checkout). Admin UI to assign branches per user (Users → Branches). Verified live: non-assigned branch edit blocked, assigned allowed, admin bypass. | Models/Domain/UserStore.cs, Services/StoreAccessService.cs, Data/ApplicationDbContext.cs (+migration), Areas/Inventory/Controllers/{Stock,Stocktake,Transfers}Controller.cs, Controllers/TillController.cs, Areas/Admin/Controllers/UsersController.cs, Areas/Admin/Views/Users/Stores.cshtml + Index.cshtml |
| FX-20 | Typed stock movements — adjustment reasons map to `Purchase`/`Damage`/`Loss` (was all `Adjustment`) | Models/Domain/StockMovement.cs, Areas/Inventory/Controllers/StockController.cs |
| FX-21 | Concurrency safety on manual adjustments + stock-take (lock + re-read + graceful catch) | Areas/Inventory/Controllers/StockController.cs, StocktakeController.cs |

---

## 🔲 Open — work these top-to-bottom

### 🔴 Critical
| ID | Item | Ref | Notes / approach |
|----|------|-----|------------------|
| OP-1 | Paystack **test** keys exposed in **git history** (commits before `4be47d9` "stop tracking dev secrets file"). Source is already clean: base `appsettings.json` = `YOUR_…` placeholders; dev file gitignored/untracked. **Remaining = USER ACTION:** rotate the test keys in the Paystack dashboard (history scrub then unnecessary since test-only). No code change. | C1 | ⚠️ user-only |
| ~~OP-2~~ | ✅ **DONE** (FX-41) — `FulfilmentRetryService` (BackgroundService, every 5 min + on startup) retries paid-but-unfulfilled **online** orders (idempotent FulfilPaidOrderAsync → self-heals transient failures) and emails the admin once for any stuck past 15 min (`Order.FulfilmentAlertedAt` dedupe); failures now stamp AdminNotes. Added a `Channel == Online` guard in both the query and FulfilPaidOrderAsync so POS sales are never re-fulfilled. Verified: a stuck online order auto-fulfilled on the next sweep; POS orders untouched; no double-deduction. | R2 / #12 | — |
| ~~OP-3~~ | ✅ **DONE** (FX-22) — `StoreInventoryConcurrencyToken` `Up()`/`Down()` neutralized to no-ops; both pending migrations applied to dev & verified (xmin still a system column, 12 CHECKs + indexes live) | D1 | — |

### 🟠 High
| ID | Item | Ref | Notes / approach |
|----|------|-----|------------------|
| ~~OP-44~~ | ✅ **DONE** (FX-32) — anonymous order-confirmation PII leak closed via Data-Protection-signed token (owner-or-token) | security audit | — |
| ~~OP-4~~ | ✅ **DONE** — Phase 1 (FX-36) per-variant balances/grid/POS + Phase 2 (FX-37) storefront availability, cart guard, **online reservation/fulfilment per variant**. (Per-variant stock-take *UI* still pool-level — minor, OP-48.) | R1 | — |
| ~~OP-5~~ | ✅ **DONE** (FX-45) — POS `Checkout` now sells against **available** (`GetAvailableAsync` = on-hand − reserved) in both the pre-check and the in-lock re-check, so the till can no longer sell units held for a pending online order. Till product search also shows `AvailableQuantity` so the cashier's number matches what's sellable. Verified (Playwright + DB) with on-hand 5 / reserved 3 (available 2): sell qty 3 → rejected "Not enough available stock"; qty 2 → sold (on-hand 5→3, reserved untouched, Sale ledger −2); qty 1 → rejected (available now 0). Test order/stock cleaned up. | #11 / I1 | — |
| ~~OP-6~~ | ✅ **DONE** (FX-43) — Paystack webhook now resolves the order by **exact** `metadata.order_number` (stamped at initiation) with a fallback to exact `PaymentReference == reference`; the loose `reference.Contains(OrderNumber)` substring match is gone. Also added an **amount-verification guard**: if the paid amount is below the order total, the order is flagged in AdminNotes and **not** auto-fulfilled (acks 200 so Paystack stops retrying). Verified with signed payloads: underpaid → flagged, not paid; substring-only reference with non-matching metadata → no match (old bug closed); bad signature → 401; exact metadata + correct amount → marked paid. | R3 | — |
| OP-7 | FK delete cascades destroy history: product delete → `OrderItems`+`StockMovements`; user delete → `Orders` | D2 | Change those FKs CASCADE→RESTRICT (behavior-affecting migration; soft-delete already exists). |
| ~~OP-8~~ | ✅ **DONE** (FX-35) — store-level (writes-only) authorization: per-user branch assignment + enforcement on stock/stock-take/transfers/till; admin assignment UI | H7 | — |
| ~~OP-9~~ | ✅ **DONE** (FX-42) — `_ValidationScriptsPartial` referenced non-existent `wwwroot/lib/jquery-validation*` (404 + MIME errors) and the CDN fallback can't load under our `script-src 'self'` CSP; jQuery isn't loaded site-wide either, so client validation never actually ran. Made the partial a no-op with a comment — these auth forms are fully validated server-side (ModelState). Verified: Login page emits **no** jquery script refs. Follow-up: vendor jquery+validation+unobtrusive locally to satisfy CSP if client-side validation is wanted. | R11 | — |
| OP-33 | **Back-in-stock notify is a no-op** — `ProductsController.NotifyRestock` only `LogInformation`s and discards the email; customers told "we'll notify you" but nothing is stored/sent (revenue leak + broken promise) | merch audit | Persist requests (table) + send on restock (hook in `StockService.ApplyAsync` when qty crosses 0→+). |
| ~~OP-28~~ | ✅ **DONE** (FX-44) — built an online-order refund workflow in Admin → Order detail (full or partial, item-level), mirroring the POS return: creates a `Refund`+`RefundItems`, optionally returns stock to the fulfilling store via the `Return` ledger (FOR UPDATE + xmin), and attempts a gateway refund (new `IPaymentService.RefundPaymentAsync`; Paystack real `/refund`, Stripe/Flutterwave report "not automated"). Best-effort gateway: the refund record + restock are authoritative, gateway failures are flagged in AdminNotes for a manual refund. Full refund → status `Refunded`; partial leaves status. Closed the cosmetic hole: `UpdateStatus` now **rejects** setting `Refunded` (removed from the dropdown + server-side guard). Verified end-to-end (Playwright + DB): partial refund restocked 10→11 with a Return ledger row, status stayed Delivered; full refund → Refunded + form hidden; gateway "Transaction not found" handled gracefully; direct `UpdateStatus=Refunded` POST left status unchanged. | admin audit | — |
| OP-31 | Guests can't view order history/tracking after leaving the confirmation page (guest account has a random password → can't log in) | storefront audit | **Partly addressed by FX-48**: a guest can now Register with their email to upgrade the shell into a full account and see all their orders (or use password reset). Remaining: a no-account magic-link order lookup for guests who never register. |

### 🟡 Medium
| ID | Item | Ref | Notes |
|----|------|-----|-------|
| ~~OP-10~~ | ✅ **DONE** (FX-33) — CSP header added (pragmatic, `'unsafe-inline'` for now) | R7 | — |
| OP-45 | CSP still needs `'unsafe-inline'` for scripts — refactor inline `<script>`/`onsubmit`/`onchange` to nonces/external handlers to drop it (full XSS hardening) | follow-up to FX-33 | Nonce middleware + move inline handlers. |
| ~~OP-11~~ | ✅ **DONE** (FX-48) — added `ApplicationUser.IsGuest` (migration `UserIsGuest`). Guest checkout now: **(1)** never attaches an order to a real registered account — if the email belongs to a non-guest account it blocks with "an account exists, please sign in/reset password" (was: silently attached → cross-person history); **(2)** reuses the existing guest shell for the same email instead of creating a new one each time (no sprawl); **(3)** on Register with a guest email, **upgrades** the shell into a full account (sets the password, `IsGuest=false`) so they keep their guest orders (the "merge"). Verified (Playwright + DB): new guest → 1 shell (IsGuest=true); repeat guest → same shell reused (2 orders, 1 user); registered email → blocked; register with guest email → upgraded + signed in + both prior orders visible. Test data cleaned up. | R4 | — |
| OP-12 | Auto-`MigrateAsync()` on production startup (bad migration → site down) | R5 | Run migrations as a gated deploy step. |
| OP-13 | Only Paystack has a webhook; Stripe/Flutterwave rely on browser callback only | R6 | Add webhooks if those providers go live. |
| OP-14 | Dev `EnsureCreated` vs Prod `Migrate` — migrations never exercised in dev | R8 | Use migrations in dev too. |
| OP-15 | Stock ledger sparse — opening balances imported w/o `StockMovement` rows (can't reconstruct stock) | D3 / #15 | Write opening-stock `Adjustment` movements on import. |
| OP-16 | No dedicated Purchase/PO module (stock receipt is a typed adjustment) + no shrinkage report | I3 | Build POs; report grouping new `Damage`/`Loss` types. |
| ~~OP-17~~ | ✅ **DONE** (FX-47) — pushed report aggregation to SQL instead of loading the whole catalogue/inventory into memory: Inventory **Reorder** (per-product totals + threshold filter in DB; per-store breakdown fetched only for products at/below threshold) + **Value** (per-branch and per-product units/value via grouped SQL); Admin **Stock** (totals/by-branch/low-stock all grouped in SQL) + **Sales** (count/gross/by-payment/by-day/by-branch grouped in SQL). Admin **Products** was already SQL-aggregated. Verified (Playwright + DB): all six pages 200 with totals matching direct SQL (Stock ₦297,300/45u/2974 oos; Sales gross ₦980,350/refunds ₦20,900/14 orders; Reorder 991). Note: ordering by a *record* property after GroupBy doesn't translate — projected to anon types then mapped/ordered in memory. Residual low-impact items (bulk/stocktake per-line writes, customers N+1, audit-export cap) left as minor follow-ups. | H10–H14 | minor residuals only |
| ~~OP-18~~ | ✅ **DONE** (FX-46) — **(a)** Explicit stronger password policy: length 8 + upper + lower + digit + 4 unique chars (was: no uppercase requirement). **(b)** Email-confirmation flow built: Register generates a token + sends a confirm link (link also logged in Development since SMTP is off), `ConfirmEmail` verifies it, `ResendConfirmation` + an unconfirmed-email banner on Profile. Enforcement (`RequireConfirmedEmail`) is deliberately left **off** — flipping it on would lock out the 3 existing unconfirmed users and depends on live SMTP; enabling it is a one-line flip once existing users are grandfathered (`EmailConfirmed=true`) and SMTP is configured (documented in Program.cs). Verified (Playwright + DB): weak pw rejected ("must have an uppercase"); strong pw registers + shows banner + EmailConfirmed=false; confirm link → EmailConfirmed=true + banner clears; invalid/missing token redirect cleanly. | H8, H9 | enforce-confirmation is the remaining opt-in step (user decision). |
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
| ~~OP-42~~ | ✅ **DONE** (FX-49) — addressed via **data-caching** rather than full-page OutputCache: full-page caching is unsafe here because every storefront page embeds a per-request antiforgery token and a per-session nav (cart badge / wishlist / auth state), which would be shared across users. Instead cached the genuinely expensive, non-personalized query — `MerchandisingService.BestSellersAsync` (a GROUP BY over the whole OrderItems ledger, called **twice per home load** for all-time + trending) and `NewArrivalsAsync` — in `IMemoryCache` with a 5-min TTL. HTML still renders per-request so nav/tokens stay correct. Verified: across 5 home loads the best-seller GROUP BY ran only **twice** (once per cache key) instead of 10×. Trade-off: best-sellers can be up to 5 min stale (fine for a merch row). Full-page OutputCache deferred (would need a client-side cart badge + token handling); featured/category queries are cheap indexed lookups left as-is. | perf audit | full-page OutputCache deferred |
| ~~OP-43~~ | ✅ **DONE** (FX-50, code side) — `UseStaticFiles` now sets `Cache-Control` via `OnPrepareResponse`: content-addressed assets get `public,max-age=31536000,immutable` — css/js carry a `?v=<hash>` (asp-append-version) and everything under `/uploads` is saved with a unique `{Guid:N}` filename (a replacement is always a new URL); other/unversioned assets (favicon) get `max-age=86400`. ETag/Last-Modified preserved (conditional GET still 304s). This makes the app CDN-ready. Verified: css `?v=` + `/uploads/*` → immutable 1yr; bare css/favicon → 1 day; `If-None-Match` → 304. **Remaining (infra, not code):** actually front `/uploads` + static with a CDN in production. | perf audit / CDN readiness | CDN provisioning is an ops task |
| ~~OP-25~~ | ✅ **DONE** (FX-42) — renamed the action `Request` → `RequestTransfer` and kept the public route via `[ActionName("Request")]`, so it no longer hides `ControllerBase.Request` (CS0108 gone — **build is now 0 warnings**). Verified: `POST /Inventory/Transfers/Request` still resolves (302 → login, not 404). | — | — |
| OP-26 | Storefront: no reviews/ratings, no abandoned-cart recovery, product images lack width/height (CLS) | H16–H18 | Conversion/SEO backlog. |
| ~~OP-29~~ | ✅ **DONE** (already) — re-checked during the FX-42 batch: `OrdersController.ExportCsv` already calls `LogAsync("Export", "Order", null, …)` before returning the file. No change needed. | admin audit | — |
| OP-41 | `wwwroot/js/app.js` (8 KB) ships unminified | perf audit | Minify in the build/asset pipeline. |
| ~~OP-46~~ | ✅ **DONE** (FX-40) — made the Postgres-isms provider-conditional: the `xmin` rowversion mapping and all 6 `FOR UPDATE` lock sites are guarded by `Database.IsNpgsql()` (active in prod, no-op on the SQLite test harness). All **13 tests pass**; Postgres lock/concurrency behaviour unchanged. | variant Phase 2 | — |
| ~~OP-47~~ | ✅ **DONE** (FX-38) — `CheckoutViewModel : IValidatableObject` requires the address only for Delivery, store only for Pickup; removed unconditional `[Required]`. Verified: a pickup order placed with no address → created + fulfilled. | variant Phase 2 | — |
| ~~OP-48~~ | ✅ **DONE** (FX-39) — stock-take sheet now has per-variant rows; variance keyed by product+variant; Apply materializes the variant row. Verified: counting a variant → variant row created. | variant Phase 2 | — |
| ~~OP-30~~ | ✅ **DONE** (FX-42) — fixed the mojibake at the **source** (`Infrastructure/SeedData.cs`, 3 stores) and in the **live DB** (`UPDATE "Stores" SET "OpeningHours"='Mon-Sat: 8am-8pm, Sun: 12pm-8pm'`, 3 rows). Used ASCII hyphens to avoid re-encoding. Verified: Stores page now renders `Mon-Sat: 8am-8pm, Sun: 12pm-8pm`. | storefront audit | — |
| OP-32 | Product detail "Reviews (0)" tab is a dead end — always "no reviews", no way to submit (no reviews feature) | storefront audit / H16 | Build reviews, or hide the tab until shipped. |
| OP-38 | Product JSON-LD lacks `priceValidUntil` / `shippingDetails` / `hasMerchantReturnPolicy` (Google Merchant rich-result enrichments) | SEO audit | Add to Detail.cshtml product schema. |
| OP-39 | BreadcrumbList JSON-LD only on product detail, not on category/listing pages | SEO audit | Add breadcrumb schema to Products/Index. |
| OP-40 | No `aggregateRating`/`review` schema (blocked on reviews feature) — do **not** fake it | SEO audit / OP-32 | Add once reviews ship. |
| ~~OP-34~~ | ✅ **DONE** (FX-51) — `MerchandisingService.FrequentlyBoughtTogetherAsync(productId, take)`: co-purchase analysis (orders containing the product → other products in those orders ranked by how many of those orders they share), `IMemoryCache` 5-min TTL. Shown as a "Frequently Bought Together" row on the product detail page (above Related), hidden when there are no co-purchases. Verified (seeded a 2-product order + DB/HTTP): band-ring ↔ pendant cross-show correctly; a product with no co-purchase shows no section. Test order cleaned up. | merch audit | — |
| ~~OP-35~~ | ✅ **DONE** (FX-52) — added `CartViewModel.SavedItems` (in the session cart) + `SaveForLater` / `MoveToBag` / `RemoveSaved` actions. Cart page shows a "Save for later" link per line and a "Saved for later" section (Move to bag / Remove); saved items don't count toward totals or checkout, and the bag-empty message only shows when both lists are empty. `MoveToBag` re-checks live availability. Verified (Playwright): Add → Save (item leaves bag, enters Saved) → Move to bag (returns) → Save + Remove (both empty). | merch audit | — |
| OP-36 | **Abandoned-cart recovery** not implemented (no cart capture/email) | merch audit / H17 | Persist cart w/ email on checkout-start; background job emails after N hrs; recovery link. |
| OP-37 | **Loyalty foundation** not implemented | merch audit | `LoyaltyAccount`/`PointsLedger` tables; accrue on paid order; redeem at checkout. |

> Full Medium/Low long-tail is in `docs/AUDIT_REPORT.md` (Medium Findings, Low Findings, Reports Gap Analysis).
