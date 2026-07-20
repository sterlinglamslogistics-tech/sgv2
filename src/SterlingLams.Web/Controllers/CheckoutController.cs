using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services.Payment;
using Microsoft.EntityFrameworkCore;

namespace SterlingLams.Web.Controllers;

public class CheckoutController : Controller
{
    private const string CartSessionKey = "cart";

    private readonly ApplicationDbContext _db;
    private readonly IPaymentService _payment;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<CheckoutController> _logger;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly SterlingLams.Web.Services.IOrderFulfilmentService _fulfilment;
    private readonly SterlingLams.Web.Services.ISettingsService _settings;
    private readonly SterlingLams.Web.Services.DeliveryZoneService _zones;
    private readonly SterlingLams.Web.Services.IDiscountService _discounts;
    private readonly SterlingLams.Web.Services.IEmailService _email;
    private readonly SterlingLams.Web.Services.IWhatsAppService _whatsapp;
    private readonly SterlingLams.Web.Services.ILoyaltyService _loyalty;
    private readonly SterlingLams.Web.Services.IGiftCardService _giftCards;
    private readonly SterlingLams.Web.Services.Logistics.ILogisticsDispatchService _logistics;
    private readonly SterlingLams.Web.Services.IStockService _stock;
    private readonly SterlingLams.Web.Services.IAuditService _audit;
    private readonly SterlingLams.Web.Services.IOrderNumberService _orderNumbers;
    private readonly IDataProtector _confirmTokenProtector;

    public CheckoutController(
        ApplicationDbContext db,
        IPaymentService payment,
        UserManager<ApplicationUser> userManager,
        ILogger<CheckoutController> logger,
        IConfiguration config,
        IWebHostEnvironment env,
        SterlingLams.Web.Services.IOrderFulfilmentService fulfilment,
        SterlingLams.Web.Services.ISettingsService settings,
        SterlingLams.Web.Services.DeliveryZoneService zones,
        SterlingLams.Web.Services.IDiscountService discounts,
        SterlingLams.Web.Services.IEmailService email,
        SterlingLams.Web.Services.IWhatsAppService whatsapp,
        SterlingLams.Web.Services.ILoyaltyService loyalty,
        SterlingLams.Web.Services.IGiftCardService giftCards,
        SterlingLams.Web.Services.Logistics.ILogisticsDispatchService logistics,
        SterlingLams.Web.Services.IStockService stock,
        SterlingLams.Web.Services.IAuditService audit,
        SterlingLams.Web.Services.IOrderNumberService orderNumbers,
        IDataProtectionProvider dataProtection)
    {
        _db = db;
        _payment = payment;
        _userManager = userManager;
        _logger = logger;
        _config = config;
        _env = env;
        _fulfilment = fulfilment;
        _settings = settings;
        _zones = zones;
        _discounts = discounts;
        _email = email;
        _whatsapp = whatsapp;
        _loyalty = loyalty;
        _giftCards = giftCards;
        _logistics = logistics;
        _stock = stock;
        _audit = audit;
        _orderNumbers = orderNumbers;
        _confirmTokenProtector = dataProtection.CreateProtector("Checkout.Confirmation.v1");
    }

    /// <summary>Opaque, tamper-proof token tying a viewer to a specific order's confirmation page —
    /// lets a guest (who isn't signed in) see their own confirmation without exposing every order
    /// to anyone who guesses an order number.</summary>
    private string ConfirmationToken(string orderNumber) => _confirmTokenProtector.Protect(orderNumber);

    private bool ConfirmationTokenValid(string orderNumber, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return _confirmTokenProtector.Unprotect(token) == orderNumber; }
        catch { return false; }
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var cart = GetCart();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        if (!await _settings.GetBoolAsync("store.accepting_orders", true))
        {
            TempData["Error"] = "We're not accepting online orders right now. Please check back soon.";
            return RedirectToAction("Index", "Cart");
        }

        var user = await _userManager.GetUserAsync(User);

        // Re-apply automatic promotion (in case the customer skipped the cart page)
        if (string.IsNullOrEmpty(cart.AppliedDiscountCode) || cart.IsAutomaticDiscount)
        {
            var auto = await _discounts.FindAutomaticAsync(cart, user?.Id);
            if (auto != null)
            {
                cart.AppliedDiscountCode = auto.Code;
                cart.DiscountDescription = auto.Description;
                cart.DiscountAmount      = auto.Amount;
                cart.FreeShipping        = auto.FreeShipping;
                cart.IsAutomaticDiscount = true;
                SaveCart(cart);
            }
        }

        var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();

        // Build delivery pricing JSON for client-side zone detection
        var pricingJson = await BuildDeliveryPricingJsonAsync();

        var vm = new CheckoutViewModel
        {
            Cart = cart,
            Subtotal = cart.Subtotal,
            DiscountAmount = cart.DiscountAmount,
            AppliedDiscountCode = cart.AppliedDiscountCode,
            DiscountDescription = cart.DiscountDescription,
            DeliveryFee = 0,   // updated client-side when delivery type selected
            DeliveryPricingJson = pricingJson,
            NigerianStates = SterlingLams.Web.Services.DeliveryZoneService.NigerianStates,
            LagosLGAs = SterlingLams.Web.Services.DeliveryZoneService.LagosLGAs,
            PaystackPublicKey = await _settings.GetAsync("payment.paystack.public_key", _config["Payment:Paystack:PublicKey"] ?? ""),
            PickupAvailable = await _settings.GetBoolAsync("store.pickup_available", true),
            AvailableStores = stores.Select(s => new StorePickupOptionViewModel
            {
                StoreId = s.Id,
                StoreName = s.Name,
                Address = s.Address,
                OpeningHours = s.OpeningHours,
                AllItemsAvailable = true
            }).ToList()
        };

        // Saved addresses (signed-in customers): list them and prefill the form with the default.
        if (user != null)
        {
            var saved = await _db.Addresses.Where(a => a.UserId == user.Id)
                .OrderByDescending(a => a.IsDefault).ThenBy(a => a.Id).ToListAsync();
            vm.SavedAddresses = saved;
            var def = saved.FirstOrDefault(a => a.IsDefault) ?? saved.FirstOrDefault();
            if (def != null)
            {
                vm.SelectedAddressId = def.Id;
                vm.DeliveryAddress = new DeliveryAddressViewModel
                {
                    FullName = def.FullName, Phone = def.Phone, Line1 = def.Line1, Line2 = def.Line2,
                    City = def.City, State = def.State, Country = def.Country, PostalCode = def.PostalCode
                };
            }
        }

        // Loyalty redemption (signed-in customers only).
        if (user != null && await _loyalty.RedemptionEnabledAsync())
        {
            var balance = await _loyalty.GetBalanceAsync(user.Id);
            if (balance > 0)
            {
                var pointValue = await _loyalty.PointValueAsync();
                vm.LoyaltyAvailable = true;
                vm.LoyaltyPointsBalance = balance;
                vm.LoyaltyPointValue = pointValue;
                // Cap the discount at the order subtotal (can't redeem more than the goods are worth).
                vm.LoyaltyMaxDiscount = Math.Min(balance * pointValue, cart.Subtotal);
            }
        }

        vm.GiftCardsAvailable = await _giftCards.RedemptionEnabledAsync();

        return View(vm);
    }

    // Build the client-side delivery-pricing JSON (zone detection + fees).
    private async Task<string> BuildDeliveryPricingJsonAsync()
    {
        // Distance zones per state (Lagos/Abuja), each with its own Standard + Express fees and the
        // areas it covers — the checkout resolves the fee from the customer's chosen area.
        var zones = await _zones.GetZonesAsync();
        var byState = zones
            .GroupBy(z => z.State, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(z => new
            {
                name = z.Name,
                standardFee = z.StandardFee, expressFee = z.ExpressFee,
                standardDays = z.StandardDays, expressDays = z.ExpressDays,
                areas = z.Areas,
            }).ToArray());

        var natStdFee  = await _settings.GetDecimalAsync("shipping.national_standard_fee", 7500);
        var natStdDays = await _settings.GetAsync("shipping.national_standard_days", "2 - 5 working days");

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            zones = byState,   // { "Lagos": [ { name, standardFee, expressFee, standardDays, expressDays, areas[] } ], "Abuja": [...] }
            national = new[]
            {
                new { type = "Standard", label = "Standard Delivery", fee = natStdFee, timeframe = natStdDays },
            },
            lagosLGAs     = SterlingLams.Web.Services.DeliveryZoneService.LagosLGAs,
            abujaKeywords = new[] { "FCT", "Abuja", "Federal Capital" },
        });
    }

    // ── Delivery-timeframe preview ──────────────────────────────────────────────
    public class DelayedItemDto
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }
        public string ProductName { get; set; } = "";
        public string SourceStore { get; set; } = "";
        public string Eta { get; set; } = "";
    }

    // Cart items that would ship slowly: the customer's nearby branch (or chosen pickup branch)
    // can't cover them, so they must come from a far branch. Powers the checkout agreement modal
    // and the server-side guard below.
    private async Task<List<DelayedItemDto>> ComputeDelayedItemsAsync(
        CartViewModel cart, FulfillmentChoice fulfilment, string? state, string? city, int? pickupStoreId)
    {
        var result = new List<DelayedItemDto>();
        if (cart.IsEmpty) return result;

        var activeStores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
        if (activeStores.Count == 0) return result;
        var crossEta = await _settings.GetAsync("shipping.cross_branch_days", "3 - 5 working days");

        async Task<Store?> NearestWithStockAsync(int pid, int? vid, int need)
        {
            var ranked = SterlingLams.Web.Services.DeliveryZoneService.RankStoresByProximity(activeStores, state, city);
            foreach (var s in ranked) if (await _stock.GetAvailableAsync(pid, vid, s.Id) >= need) return s;
            foreach (var s in ranked) if (await _stock.GetAvailableAsync(pid, vid, s.Id) > 0) return s;
            return null;
        }

        foreach (var grp in cart.Items.GroupBy(i => (i.ProductId, i.VariantId)))
        {
            var pid = grp.Key.ProductId; var vid = grp.Key.VariantId;
            var need = grp.Sum(i => i.Quantity);
            var name = grp.First().ProductName;

            if (fulfilment == FulfillmentChoice.StorePickup)
            {
                if (!pickupStoreId.HasValue) continue;
                if (await _stock.GetAvailableAsync(pid, vid, pickupStoreId.Value) >= need) continue; // ready at chosen branch
                var src = await NearestWithStockAsync(pid, vid, need);
                result.Add(new DelayedItemDto { ProductId = pid, VariantId = vid, ProductName = name,
                    SourceStore = src?.Name.Replace("Sterlin Glams ", "") ?? "another branch", Eta = crossEta });
            }
            else // delivery: "near" = covered by a branch inside the customer's delivery zone
            {
                var zone = SterlingLams.Web.Services.DeliveryZoneService.GetZone(state ?? "");
                // The far-stock agreement only applies to Lagos & Abuja customers (the zones with a
                // physical store, where buyers expect fast local delivery). Customers in other states
                // already expect the standard nationwide timeframe, so never prompt them.
                if (zone == SterlingLams.Web.Services.DeliveryZone.National) continue;
                var nearAvail = 0;
                foreach (var s in activeStores.Where(s => SterlingLams.Web.Services.DeliveryZoneService.GetZone(s.State) == zone))
                    nearAvail += await _stock.GetAvailableAsync(pid, vid, s.Id);
                if (nearAvail >= need) continue;
                var src = await NearestWithStockAsync(pid, vid, need);
                result.Add(new DelayedItemDto { ProductId = pid, VariantId = vid, ProductName = name,
                    SourceStore = src?.Name.Replace("Sterlin Glams ", "") ?? "another branch", Eta = crossEta });
            }
        }
        return result;
    }

    // AJAX: the checkout page calls this when the customer clicks "Proceed to Payment" to decide
    // whether to show the timeframe-agreement modal.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> FulfilmentPreview(string? fulfillmentType, string? state, string? city, int? storeId)
    {
        var cart = GetCart();
        var choice = string.Equals(fulfillmentType, "StorePickup", StringComparison.OrdinalIgnoreCase)
            ? FulfillmentChoice.StorePickup : FulfillmentChoice.Delivery;
        var delayed = await ComputeDelayedItemsAsync(cart, choice, state, city, storeId);
        return Json(new { delayed = delayed.Select(d => new { d.ProductId, d.VariantId, d.ProductName, d.SourceStore, d.Eta }) });
    }

    // Re-populate the display-only fields the checkout view needs (states, stores, pricing, totals,
    // loyalty). POST model binding only fills the submitted form fields, so this MUST run before
    // re-rendering the checkout view on a validation error — otherwise the State dropdown, delivery
    // options and order summary all come back empty.
    private async Task RehydrateCheckoutDisplayAsync(CheckoutViewModel vm)
    {
        var cart = GetCart();
        var user = await _userManager.GetUserAsync(User);

        vm.Cart                = cart;
        vm.Subtotal            = cart.Subtotal;
        vm.DiscountAmount      = cart.DiscountAmount;
        vm.AppliedDiscountCode = cart.AppliedDiscountCode;
        vm.DiscountDescription = cart.DiscountDescription;
        vm.DeliveryPricingJson = await BuildDeliveryPricingJsonAsync();
        vm.NigerianStates      = SterlingLams.Web.Services.DeliveryZoneService.NigerianStates;
        vm.LagosLGAs           = SterlingLams.Web.Services.DeliveryZoneService.LagosLGAs;
        vm.PaystackPublicKey   = await _settings.GetAsync("payment.paystack.public_key", _config["Payment:Paystack:PublicKey"] ?? "");
        vm.PickupAvailable     = await _settings.GetBoolAsync("store.pickup_available", true);
        vm.AvailableStores     = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
            .Select(s => new StorePickupOptionViewModel
            {
                StoreId = s.Id, StoreName = s.Name, Address = s.Address,
                OpeningHours = s.OpeningHours, AllItemsAvailable = true
            }).ToList();

        if (user != null && await _loyalty.RedemptionEnabledAsync())
        {
            var balance = await _loyalty.GetBalanceAsync(user.Id);
            if (balance > 0)
            {
                var pointValue = await _loyalty.PointValueAsync();
                vm.LoyaltyAvailable     = true;
                vm.LoyaltyPointsBalance = balance;
                vm.LoyaltyPointValue    = pointValue;
                vm.LoyaltyMaxDiscount   = Math.Min(balance * pointValue, cart.Subtotal);
            }
        }

        vm.GiftCardsAvailable = await _giftCards.RedemptionEnabledAsync();
    }

    // Re-render checkout after a validation error, with all display data repopulated.
    private async Task<IActionResult> RedisplayCheckoutAsync(CheckoutViewModel vm)
    {
        await RehydrateCheckoutDisplayAsync(vm);
        return View("Index", vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PlaceOrder(CheckoutViewModel vm)
    {
        // Store pickup doesn't use the delivery address, but the form still POSTs those fields empty.
        // They're non-nullable strings, so the framework's implicit "required" fails them ("The State
        // field is required") and the order silently bounced back to checkout ("just reloads"). Drop
        // those errors for pickup so it can proceed to payment. (Delivery still validates them via
        // CheckoutViewModel.Validate.)
        if (vm.FulfillmentType == FulfillmentChoice.StorePickup)
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith("DeliveryAddress", StringComparison.Ordinal)).ToList())
                ModelState.Remove(key);

        if (!ModelState.IsValid) return await RedisplayCheckoutAsync(vm);

        var cart = GetCart();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        // Store-level gates (admin-toggled in Settings → Store).
        if (!await _settings.GetBoolAsync("store.accepting_orders", true))
        {
            TempData["Error"] = "We're not accepting online orders right now. Please check back soon.";
            return RedirectToAction("Index", "Cart");
        }
        if (vm.FulfillmentType == FulfillmentChoice.StorePickup
            && !await _settings.GetBoolAsync("store.pickup_available", true))
        {
            TempData["Error"] = "In-store pickup isn't available right now. Please choose delivery.";
            return RedirectToAction("Index");
        }

        // Minimum order value (0 = no minimum).
        var minOrder = await _settings.GetDecimalAsync("order.min_value", 0);
        if (minOrder > 0 && cart.Subtotal < minOrder)
        {
            ModelState.AddModelError("", $"Minimum order value is {await _settings.GetAsync("store.currency_symbol", "₦")}{minOrder:N0}. Please add a little more to your bag.");
            return await RedisplayCheckoutAsync(vm);
        }

        // ── Resolve user (authenticated or guest) ──────────────────────────
        ApplicationUser? user = await _userManager.GetUserAsync(User);

        if (user == null)
        {
            // Guest checkout: require contact fields
            if (string.IsNullOrWhiteSpace(vm.GuestEmail))
            {
                ModelState.AddModelError("GuestEmail", "Please enter your email address.");
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return await RedisplayCheckoutAsync(vm);
            }

            // Resolve the guest's account by email — but never silently attach this order to a real
            // registered account (that would leak orders into a stranger's history). Reuse an existing
            // *guest* shell for the same email (no account sprawl); create one only if none exists.
            var existing = await _userManager.FindByEmailAsync(vm.GuestEmail);
            if (existing != null && !existing.IsGuest)
            {
                ModelState.AddModelError("GuestEmail",
                    "An account already exists with this email. Please sign in to place your order (or reset your password if you've forgotten it).");
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return await RedisplayCheckoutAsync(vm);
            }

            user = existing;
            if (user == null)
            {
                var guestName = vm.GuestName?.Trim() ?? "Guest";
                var nameParts = guestName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                user = new ApplicationUser
                {
                    UserName  = vm.GuestEmail,
                    Email     = vm.GuestEmail,
                    FirstName = nameParts.Length > 0 ? nameParts[0] : "Guest",
                    LastName  = nameParts.Length > 1 ? nameParts[1] : string.Empty,
                    PhoneNumber = vm.GuestPhone,
                    IsGuest   = true,
                    CreatedAt = DateTime.UtcNow
                };
                var createResult = await _userManager.CreateAsync(user, Guid.NewGuid().ToString("N") + "Aa1!");
                if (!createResult.Succeeded)
                {
                    ModelState.AddModelError("", "Unable to process guest checkout. Please try again.");
                    vm.Cart = cart;
                    vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
                        .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                    return await RedisplayCheckoutAsync(vm);
                }
                _logger.LogInformation("Guest account created for checkout: {Email}", SterlingLams.Web.Infrastructure.LogRedact.Email(vm.GuestEmail));
            }
        }

        // Validate store selection for pickup orders
        if (vm.FulfillmentType == FulfillmentChoice.StorePickup)
        {
            if (vm.SelectedStoreId == null || !await _db.Stores.AnyAsync(s => s.Id == vm.SelectedStoreId && s.IsActive))
            {
                ModelState.AddModelError("SelectedStoreId", "Please select a valid store for pickup.");
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return await RedisplayCheckoutAsync(vm);
            }
        }

        // Validate that all product IDs exist and are active
        var productIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();
        var validProducts = await _db.Products
            .Where(p => productIds.Contains(p.Id) && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync();

        if (validProducts.Count != productIds.Count)
        {
            TempData["Error"] = "One or more items in your cart are no longer available. Please review your bag.";
            return RedirectToAction("Index", "Cart");
        }

        // Overselling is guarded by reserving stock once the order is saved (see below) — that
        // hold is atomic and blocks concurrent orders from claiming the same units.

        // Re-validate the discount server-side (never trust the cached cart amount)
        decimal discountAmount = 0;
        bool   freeShipping    = false;
        string? discountCode   = null;
        if (!string.IsNullOrEmpty(cart.AppliedDiscountCode))
        {
            var dr = cart.IsAutomaticDiscount
                ? await _discounts.FindAutomaticAsync(cart, user.Id)
                : await _discounts.EvaluateAsync(cart.AppliedDiscountCode, cart, user.Id);
            if (dr != null && dr.Success)
            {
                discountCode   = dr.Code;
                discountAmount = dr.Amount;
                freeShipping   = dr.FreeShipping;
            }
        }

        // Calculate delivery fee server-side (never trust client-submitted amount)
        decimal deliveryFee = 0;
        if (vm.FulfillmentType == FulfillmentChoice.Delivery)
            deliveryFee = await _zones.CalculateFeeAsync(vm.DeliveryAddress.State, vm.DeliveryAddress.City, vm.SelectedDeliveryType);
        if (freeShipping) deliveryFee = 0;   // free-shipping discount waives the fee

        // ── Loyalty redemption ──────────────────────────────────────────────
        // Earmark points + discount now (reduces the amount charged); the actual point deduction
        // happens on payment success (RedeemForOrderAsync) so an abandoned order never loses points.
        int loyaltyPoints = 0;
        decimal loyaltyDiscount = 0m;
        if (vm.RedeemPoints && await _loyalty.RedemptionEnabledAsync())
        {
            var balance = await _loyalty.GetBalanceAsync(user.Id);
            if (balance > 0)
            {
                var pointValue = await _loyalty.PointValueAsync();
                var preTotal = cart.Subtotal - discountAmount + deliveryFee;
                // Cap by points held, by the goods value (after promo), and leave ≥₦1 to charge.
                var cap = Math.Min(Math.Min(balance * pointValue, cart.Subtotal - discountAmount), preTotal - 1m);
                if (cap > 0)
                {
                    loyaltyPoints = (int)Math.Floor(cap / pointValue);
                    loyaltyDiscount = loyaltyPoints * pointValue;
                }
            }
        }

        // ── Gift card redemption ────────────────────────────────────────────
        // Drawn from whatever is left to pay after promo + loyalty. Earmark now; the actual
        // balance draw happens on payment success (RedeemForOrderAsync) so an abandoned order
        // never drains the card. We leave ≥₦1 to charge so the gateway always has a positive
        // amount (full gift-card payment / zero-total checkout is a deferred enhancement).
        string? giftCardCode = null;
        decimal giftCardAmount = 0m;
        if (!string.IsNullOrWhiteSpace(vm.GiftCardCode) && await _giftCards.RedemptionEnabledAsync())
        {
            var lookup = await _giftCards.ValidateAsync(vm.GiftCardCode);
            if (!lookup.Ok)
            {
                ModelState.AddModelError("GiftCardCode", lookup.Message);
                return await RedisplayCheckoutAsync(vm);
            }
            var dueBeforeCard = cart.Subtotal - discountAmount + deliveryFee - loyaltyDiscount;
            var cap = Math.Min(lookup.Balance, dueBeforeCard - 1m);
            if (cap > 0)
            {
                giftCardAmount = Math.Round(cap, 2);
                giftCardCode = lookup.Code;
            }
        }

        // Build order — short sequential number, e.g. SL-30012.
        var orderNumber = await _orderNumbers.NextAsync(OrderChannel.Online);

        var order = new Order
        {
            OrderNumber = orderNumber,
            UserId = user.Id,
            FulfillmentType = vm.FulfillmentType == FulfillmentChoice.StorePickup
                ? FulfillmentType.StorePickup
                : FulfillmentType.Delivery,
            PickupStoreId = vm.FulfillmentType == FulfillmentChoice.StorePickup ? vm.SelectedStoreId : null,
            Notes = string.IsNullOrWhiteSpace(vm.OrderNotes) ? null : vm.OrderNotes.Trim(),
            // Order attribution (WooCommerce-style)
            CustomerIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceType = SterlingLams.Web.Infrastructure.OrderAttributionMiddleware.DeviceFromUserAgent(Request.Headers.UserAgent.ToString()),
            Origin = HttpContext.Session.GetString(SterlingLams.Web.Infrastructure.OrderAttributionMiddleware.OriginKey) ?? "Direct",
            SessionPageViews = HttpContext.Session.GetInt32(SterlingLams.Web.Infrastructure.OrderAttributionMiddleware.PageViewsKey),
            Subtotal = cart.Subtotal,
            DeliveryFee = deliveryFee,
            DeliveryType = vm.FulfillmentType == FulfillmentChoice.Delivery && !string.IsNullOrWhiteSpace(vm.SelectedDeliveryType)
                ? vm.SelectedDeliveryType.Trim() : null,
            DiscountCode = discountCode,
            DiscountAmount = discountAmount,
            LoyaltyPointsRedeemed = loyaltyPoints,
            LoyaltyDiscount = loyaltyDiscount,
            GiftCardCode = giftCardCode,
            GiftCardAmount = giftCardAmount,
            Total = cart.Subtotal - discountAmount + deliveryFee - loyaltyDiscount - giftCardAmount,
            Items = cart.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                ProductVariantId = i.VariantId,
                ProductName = i.ProductName,
                VariantName = i.VariantName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        if (vm.FulfillmentType == FulfillmentChoice.Delivery)
        {
            var addr = new Address
            {
                UserId = user.Id,
                FullName = vm.DeliveryAddress.FullName,
                Phone = vm.DeliveryAddress.Phone,
                Line1 = vm.DeliveryAddress.Line1,
                Line2 = vm.DeliveryAddress.Line2,
                City = vm.DeliveryAddress.City,
                State = vm.DeliveryAddress.State,
                Country = vm.DeliveryAddress.Country,
                PostalCode = vm.DeliveryAddress.PostalCode
            };
            _db.Addresses.Add(addr);
            await _db.SaveChangesAsync();
            order.DeliveryAddressId = addr.Id;
        }

        // Stock is never held before payment (first-come-first-served). Re-check live availability
        // right before sending the customer to pay, so if an item already sold out since the cart
        // was loaded we block here — the second buyer sees "sold out" instead of paying. (A truly
        // simultaneous payment for the last unit still slips through and is auto-refunded at the
        // callback; this check stops the common "clicked pay after it sold out" case.)
        var activeStoreIds = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();
        foreach (var grp in cart.Items.GroupBy(i => (i.ProductId, i.VariantId)))
        {
            var need = grp.Sum(i => i.Quantity);
            var have = 0;
            foreach (var sid in activeStoreIds)
                have += await _stock.GetAvailableAsync(grp.Key.ProductId, grp.Key.VariantId, sid);
            if (have < need)
            {
                TempData["Error"] = $"Sorry — \"{grp.First().ProductName}\" just sold out. Please review your bag.";
                return RedirectToAction("Index", "Cart");
            }
        }

        // Far-stock delivery timeframe: if any item must ship from a branch far from the customer,
        // they must have acknowledged the longer ETA in the modal. (The client shows it; this is the
        // server-side backstop in case the modal is bypassed.)
        var delayedItems = await ComputeDelayedItemsAsync(cart, vm.FulfillmentType,
            vm.DeliveryAddress?.State, vm.DeliveryAddress?.City, vm.SelectedStoreId);
        if (delayedItems.Count > 0 && !vm.TimeframeAcknowledged)
        {
            ModelState.AddModelError("", "Please acknowledge the delivery timeframe for items shipping from another branch.");
            return await RedisplayCheckoutAsync(vm);
        }

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id,
            $"Order placed by customer ({(order.FulfillmentType == FulfillmentType.StorePickup ? "store pickup" : "delivery")}). Awaiting payment.");
        await _db.SaveChangesAsync();

        try { await _audit.LogAsync("Order", "Order", order.Id.ToString(), $"Online order placed {order.OrderNumber} — ₦{order.Total:N0}"); } catch { }

        // Newsletter opt-in (deduped) when the customer ticked the box.
        if (vm.SubscribeNewsletter && !string.IsNullOrWhiteSpace(user.Email))
        {
            var subEmail = user.Email.Trim().ToLowerInvariant();
            if (!await _db.NewsletterSubscribers.AnyAsync(s => s.Email == subEmail))
            {
                _db.NewsletterSubscribers.Add(new Models.Domain.NewsletterSubscriber { Email = subEmail, CreatedAt = DateTime.UtcNow });
                await _db.SaveChangesAsync();
            }
        }

        // No stock is held before payment — it's committed first-come-first-served when payment
        // lands (FulfilPaidOrderAsync). If an item sells out before this customer pays, the
        // payment is auto-cancelled + refunded at the callback.

        // Snapshot the cart for abandoned-cart recovery (emailed later if payment isn't completed).
        await CaptureAbandonedCartAsync(user.Email, cart);

        // Initiate payment
        var callbackUrl = Url.Action("PaymentCallback", "Checkout", null, Request.Scheme) ?? string.Empty;
        var result = await _payment.InitiatePaymentAsync(new InitiatePaymentRequest
        {
            OrderNumber = order.OrderNumber,
            Amount = order.Total,
            Currency = order.Currency,
            CustomerEmail = user.Email ?? string.Empty,
            CustomerName = user.FullName,
            CallbackUrl = callbackUrl,
            Metadata = new Dictionary<string, string> { ["order_id"] = order.Id.ToString() }
        });

        if (!result.Success)
        {
            _logger.LogError("Payment initiation failed for order {OrderNumber}: {Error}", orderNumber, result.ErrorMessage);

            // In Development, bypass payment gateway and simulate a successful payment
            if (_env.IsDevelopment())
            {
                _logger.LogWarning("[DEV MODE] Redirecting to simulated payment for order {OrderNumber}", orderNumber);
                return RedirectToAction("DevConfirm", new { orderId = order.Id });
            }

            ModelState.AddModelError("", "Payment could not be initiated. Please try again.");
            return await RedisplayCheckoutAsync(vm);
        }

        return Redirect(result.AuthorizationUrl!);
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> PaymentCallback(string reference, string trxref)
    {
        var refToVerify = reference ?? trxref;
        if (string.IsNullOrEmpty(refToVerify)) return RedirectToAction("Index", "Home");

        var result = await _payment.VerifyPaymentAsync(refToVerify);

        if (!result.IsPaid)
        {
            // Payment failed — free the reserved stock so it returns to sale.
            var failed = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == result.OrderNumber);
            if (failed != null) await _fulfilment.ReleaseReservationAsync(failed.Id);
            TempData["Error"] = "Payment could not be verified. Please contact support.";
            return RedirectToAction("Index", "Cart");
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == result.OrderNumber);
        if (order != null)
        {
            var wasUnpaid = !order.IsPaid;
            order.IsPaid = true;
            order.PaidAt = DateTime.UtcNow;
            order.Status = OrderStatus.Confirmed;
            order.PaymentReference = refToVerify;
            order.PaymentProvider = _payment.ProviderName;
            if (wasUnpaid)
                SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id,
                    $"Payment via {_payment.ProviderName} successful (Transaction Reference: {refToVerify}).");
            await _db.SaveChangesAsync();
            if (wasUnpaid)
                try { await _audit.LogAsync("Payment", "Order", order.Id.ToString(), $"Payment received for {order.OrderNumber} — ₦{order.Total:N0} ({_payment.ProviderName})"); } catch { }

            // Commit stock first-come-first-served. If an item sold out before this payment
            // landed, auto-cancel + refund instead of confirming.
            var outcome = await _fulfilment.FulfilPaidOrderAsync(order.Id);
            if (outcome == SterlingLams.Web.Services.FulfilOutcome.SoldOut)
            {
                // Stock was committed first-come-first-served; this payment lost the race. The
                // fulfilment service already cancelled + refunded — just tell the customer.
                TempData["Error"] = $"Sorry — an item in order {order.OrderNumber} sold out just before your payment completed. You've been refunded in full.";
                HttpContext.Session.Remove(CartSessionKey);
                return RedirectToAction("Confirmation", new { orderNumber = result.OrderNumber, token = ConfirmationToken(result.OrderNumber!) });
            }

            await IncrementDiscountUsageAsync(order);
            await _loyalty.RedeemForOrderAsync(order.Id);
            await _giftCards.RedeemForOrderAsync(order.Id);
            await _loyalty.AccrueForOrderAsync(order.Id);
            await _logistics.PushOrderAsync(order.Id);

            await SendOrderEmailsAsync(order.Id);
        }

        // Clear cart
        HttpContext.Session.Remove(CartSessionKey);

        return RedirectToAction("Confirmation", new { orderNumber = result.OrderNumber, token = ConfirmationToken(result.OrderNumber!) });
    }

    /// <summary>
    /// DEV ONLY — simulates a successful payment, confirms the order, and runs in-house fulfilment.
    /// Not available in Production.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DevConfirm(int orderId)
    {
        if (!_env.IsDevelopment()) return NotFound();

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == user.Id);

        if (order == null) return NotFound();

        // Mark as paid
        order.IsPaid = true;
        order.PaidAt = DateTime.UtcNow;
        order.Status = OrderStatus.Confirmed;
        order.PaymentReference = $"SIM-DEV-{order.OrderNumber}";
        order.PaymentProvider = "Simulated (Dev Only)";
        await _db.SaveChangesAsync();

        var outcome = await _fulfilment.FulfilPaidOrderAsync(order.Id);
        if (outcome == SterlingLams.Web.Services.FulfilOutcome.SoldOut)
        {
            TempData["Error"] = $"Sorry — an item in order {order.OrderNumber} sold out just before your payment completed. You've been refunded in full.";
            HttpContext.Session.Remove(CartSessionKey);
            return RedirectToAction("Confirmation", new { orderNumber = order.OrderNumber, token = ConfirmationToken(order.OrderNumber) });
        }

        await IncrementDiscountUsageAsync(order);
        await _loyalty.RedeemForOrderAsync(order.Id);
        await _giftCards.RedeemForOrderAsync(order.Id);
        await _loyalty.AccrueForOrderAsync(order.Id);
        await _logistics.PushOrderAsync(order.Id);

        await SendOrderEmailsAsync(order.Id);

        HttpContext.Session.Remove(CartSessionKey);
        return RedirectToAction("Confirmation", new { orderNumber = order.OrderNumber, token = ConfirmationToken(order.OrderNumber) });
    }

    /// <summary>
    /// Emails the customer an order confirmation and (optionally) alerts the admin of a new order.
    /// Respects the Notifications settings toggles. Never throws — email must not break checkout.
    /// </summary>
    private async Task SendOrderEmailsAsync(int orderId)
    {
        try
        {
            var order = await _db.Orders.Include(o => o.Items)
                .Include(o => o.DeliveryAddress).Include(o => o.PickupStore).Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return;

            var customerEmail = order.User?.Email
                ?? await _db.Users.Where(u => u.Id == order.UserId).Select(u => u.Email).FirstOrDefaultAsync();

            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var rows = string.Join("", order.Items.Select(i => $@"
                <tr>
                    <td style=""padding:8px 0;border-bottom:1px solid #f0efed;"">{Enc(i.ProductName)}{(string.IsNullOrWhiteSpace(i.VariantName) ? "" : " — " + Enc(i.VariantName))} &times; {i.Quantity}</td>
                    <td align=""right"" style=""padding:8px 0;border-bottom:1px solid #f0efed;white-space:nowrap;"">&#8358;{i.LineTotal:N0}</td>
                </tr>"));
            var summary = $@"
                <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:20px 0;font-size:14px;"">
                    {rows}
                    <tr><td style=""padding:12px 0 0;font-weight:bold;"">Total</td><td align=""right"" style=""padding:12px 0 0;font-weight:bold;"">&#8358;{order.Total:N0}</td></tr>
                </table>";

            // Customer confirmation — rich WooCommerce-style layout (editable in Email Customizer).
            if (!string.IsNullOrWhiteSpace(customerEmail)
                && await _settings.GetBoolAsync("notifications.order_confirmed", true))
            {
                var subject = await _settings.GetAsync("email.order_confirmed.subject", "Your order is being processed");
                var intro = await _settings.GetAsync("email.order_confirmed.intro",
                    "Your order {order} ({date}) has been received and is now being processed.");

                // Per-item primary image (absolute URL for email clients).
                var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
                var pids = order.Items.Select(i => i.ProductId).Distinct().ToList();
                var imgMap = await _db.ProductImages.Where(im => pids.Contains(im.ProductId))
                    .GroupBy(im => im.ProductId)
                    .Select(g => new { Pid = g.Key, Url = g.OrderByDescending(x => x.IsPrimary).Select(x => x.Url).FirstOrDefault() })
                    .ToDictionaryAsync(x => x.Pid, x => x.Url);
                string? AbsImg(int pid)
                {
                    var u = imgMap.GetValueOrDefault(pid);
                    if (string.IsNullOrWhiteSpace(u)) return null;
                    return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u
                         : (string.IsNullOrEmpty(baseUrl) ? null : baseUrl + "/" + u.TrimStart('/'));
                }

                var items = order.Items.Select(i =>
                    new SterlingLams.Web.Services.OrderEmailTemplate.Item(
                        i.ProductName, i.VariantName, i.Quantity, i.LineTotal, AbsImg(i.ProductId))).ToList();

                var custName = order.User?.FullName ?? order.DeliveryAddress?.FullName ?? "";
                var a = order.DeliveryAddress;
                var billing = new List<string> { custName };
                if (a != null)
                {
                    billing.Add(a.Line1 + (string.IsNullOrWhiteSpace(a.Line2) ? "" : ", " + a.Line2));
                    billing.Add($"{a.City}, {a.State}".Trim(' ', ','));
                    if (!string.IsNullOrWhiteSpace(a.Phone)) billing.Add(a.Phone);
                }
                if (!string.IsNullOrWhiteSpace(customerEmail)) billing.Add(customerEmail!);

                List<string> shipping;
                string shippingLabel;
                if (order.FulfillmentType == FulfillmentType.StorePickup)
                {
                    var store = order.PickupStore?.Name ?? "our store";
                    shipping = new List<string> { custName, "Pickup at " + store };
                    shippingLabel = $"Pickup at {store}";
                }
                else
                {
                    shipping = new List<string> { custName };
                    if (a != null) { shipping.Add(a.Line1 + (string.IsNullOrWhiteSpace(a.Line2) ? "" : ", " + a.Line2)); shipping.Add($"{a.City}, {a.State}".Trim(' ', ',')); }
                    shippingLabel = order.DeliveryFee > 0 ? $"Delivery — ₦{order.DeliveryFee:N0}" : "Delivery";
                }

                var introHtml = SterlingLams.Web.Services.OrderEmailTemplate.ApplyPlaceholders(
                    intro, "#" + order.OrderNumber, order.CreatedAt, custName);
                var body = SterlingLams.Web.Services.OrderEmailTemplate.Build(
                    heading: subject,
                    introHtml: introHtml,
                    orderNumber: order.OrderNumber,
                    orderDate: order.CreatedAt,
                    items: items,
                    subtotal: order.Subtotal,
                    shippingLabel: shippingLabel,
                    total: order.Total,
                    paymentMethod: order.PaymentProvider ?? "—",
                    billingLines: billing,
                    shippingLines: shipping);
                await _email.SendAsync(customerEmail!, subject, body, ct: HttpContext.RequestAborted);
            }

            // WhatsApp order confirmation — self-gated by whatsapp.notify.order_confirmed + a customer
            // phone, independent of the email setting above. Fire-and-forget (own scope, never throws).
            _ = _whatsapp.NotifyOrderAsync(order.Id, SterlingLams.Web.Services.WhatsAppOrderEvent.OrderConfirmed);

            // Admin new-order alert
            if (await _settings.GetBoolAsync("notifications.new_order", true))
            {
                var adminEmail = await _settings.GetAsync("notifications.admin_email", "");
                if (!string.IsNullOrWhiteSpace(adminEmail))
                {
                    var body = $@"
                        <h2 style=""font-size:18px;margin:0 0 16px;"">New order received</h2>
                        <p>Order <strong>{Enc(order.OrderNumber)}</strong>{(string.IsNullOrWhiteSpace(customerEmail) ? "" : " from " + Enc(customerEmail))}.</p>
                        {summary}
                        <p style=""font-size:13px;color:#78716c;"">View it in the admin dashboard under Orders.</p>";
                    await _email.SendAsync(adminEmail, $"New order {order.OrderNumber} — ₦{order.Total:N0}", body, ct: HttpContext.RequestAborted);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending order emails for order {OrderId}", orderId);
        }
    }

    /// <summary>Increments the global usage count on the discount code an order used.</summary>
    private async Task IncrementDiscountUsageAsync(Order order)
    {
        if (string.IsNullOrEmpty(order.DiscountCode)) return;
        try
        {
            var dc = await _db.DiscountCodes.FirstOrDefaultAsync(d => d.Code == order.DiscountCode);
            if (dc != null)
            {
                dc.UsedCount++;
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to increment discount usage for {Code}: {Message}",
                order.DiscountCode, ex.Message);
        }
    }


    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Confirmation(string orderNumber, string? token = null)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);

        if (order == null) return NotFound();

        // Authorise the viewer: the signed-in owner, or anyone holding the order's confirmation
        // token (issued only on the post-payment redirect). Without this, an anonymous visitor
        // could read any order's PII (name/address/phone) just by guessing the order number.
        var userId = _userManager.GetUserId(User);
        var isOwner = userId != null && order.UserId == userId;
        if (!isOwner && !ConfirmationTokenValid(orderNumber, token))
            return NotFound();

        return View(order);
    }

    private CartViewModel GetCart()
    {
        var json = HttpContext.Session.GetString(CartSessionKey);
        if (string.IsNullOrEmpty(json)) return new CartViewModel();
        return JsonSerializer.Deserialize<CartViewModel>(json) ?? new CartViewModel();
    }

    private void SaveCart(CartViewModel cart) =>
        HttpContext.Session.SetString(CartSessionKey, JsonSerializer.Serialize(cart));

    /// <summary>Upserts the abandoned-cart snapshot for an email (one row each), refreshing items +
    /// resetting the clock so the recovery email fires only if this checkout isn't completed.</summary>
    private async Task CaptureAbandonedCartAsync(string? email, CartViewModel cart)
    {
        if (string.IsNullOrWhiteSpace(email) || cart.IsEmpty) return;
        var snapshot = JsonSerializer.Serialize(
            cart.Items.Select(i => new { i.ProductId, i.VariantId, i.Quantity }));
        var now = DateTime.UtcNow;

        var existing = await _db.AbandonedCarts.FirstOrDefaultAsync(a => a.Email == email);
        if (existing == null)
        {
            _db.AbandonedCarts.Add(new SterlingLams.Web.Models.Domain.AbandonedCart
            {
                Email = email, Token = Guid.NewGuid().ToString("N"),
                ItemsJson = snapshot, Subtotal = cart.Subtotal, ItemCount = cart.TotalItems, CreatedAt = now
            });
        }
        else
        {
            existing.Token = Guid.NewGuid().ToString("N");
            existing.ItemsJson = snapshot; existing.Subtotal = cart.Subtotal; existing.ItemCount = cart.TotalItems;
            existing.CreatedAt = now; existing.EmailedAt = null; existing.RecoveredAt = null;
        }
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); } // benign race on the unique email
    }
}
