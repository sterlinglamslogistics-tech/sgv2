using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services.ERPNext;
using SterlingLams.Web.Services.ERPNext.ERPNextModels;
using SterlingLams.Web.Services.Payment;
using Microsoft.EntityFrameworkCore;

namespace SterlingLams.Web.Controllers;

[Authorize]
public class CheckoutController : Controller
{
    private const string CartSessionKey = "cart";

    private readonly ApplicationDbContext _db;
    private readonly IPaymentService _payment;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<CheckoutController> _logger;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly IERPNextService _erpNext;

    public CheckoutController(
        ApplicationDbContext db,
        IPaymentService payment,
        UserManager<ApplicationUser> userManager,
        ILogger<CheckoutController> logger,
        IConfiguration config,
        IWebHostEnvironment env,
        IERPNextService erpNext)
    {
        _db = db;
        _payment = payment;
        _userManager = userManager;
        _logger = logger;
        _config = config;
        _env = env;
        _erpNext = erpNext;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var cart = GetCart();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
        var user = await _userManager.GetUserAsync(User);

        var vm = new CheckoutViewModel
        {
            Cart = cart,
            Subtotal = cart.Subtotal,
            DiscountAmount = cart.DiscountAmount,
            AppliedDiscountCode = cart.AppliedDiscountCode,
            DiscountDescription = cart.DiscountDescription,
            DeliveryFee = 0,
            PaystackPublicKey = _config["Payment:Paystack:PublicKey"],
            AvailableStores = stores.Select(s => new StorePickupOptionViewModel
            {
                StoreId = s.Id,
                StoreName = s.Name,
                Address = s.Address,
                OpeningHours = s.OpeningHours,
                AllItemsAvailable = true
            }).ToList()
        };

        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> PlaceOrder(CheckoutViewModel vm)
    {
        if (!ModelState.IsValid) return View("Index", vm);

        var cart = GetCart();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        // Validate store selection for pickup orders
        if (vm.FulfillmentType == FulfillmentChoice.StorePickup)
        {
            if (vm.SelectedStoreId == null || !await _db.Stores.AnyAsync(s => s.Id == vm.SelectedStoreId && s.IsActive))
            {
                ModelState.AddModelError("SelectedStoreId", "Please select a valid store for pickup.");
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return View("Index", vm);
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

        // Build order
        var orderNumber = $"SL-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";

        var order = new Order
        {
            OrderNumber = orderNumber,
            UserId = user.Id,
            FulfillmentType = vm.FulfillmentType == FulfillmentChoice.StorePickup
                ? FulfillmentType.StorePickup
                : FulfillmentType.Delivery,
            PickupStoreId = vm.FulfillmentType == FulfillmentChoice.StorePickup ? vm.SelectedStoreId : null,
            Subtotal = cart.Subtotal,
            DeliveryFee = vm.DeliveryFee,
            Total = cart.Subtotal + vm.DeliveryFee,
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

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

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
            return View("Index", vm);
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
            TempData["Error"] = "Payment could not be verified. Please contact support.";
            return RedirectToAction("Index", "Cart");
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == result.OrderNumber);
        if (order != null)
        {
            order.IsPaid = true;
            order.PaidAt = DateTime.UtcNow;
            order.Status = OrderStatus.Confirmed;
            order.PaymentReference = refToVerify;
            order.PaymentProvider = _payment.ProviderName;
            await _db.SaveChangesAsync();

            // Push stock deduction to ERPNext
            await PushOrderToERPNextAsync(order);
        }

        // Clear cart
        HttpContext.Session.Remove(CartSessionKey);

        return RedirectToAction("Confirmation", new { orderNumber = result.OrderNumber });
    }

    /// <summary>
    /// DEV ONLY — simulates a successful payment, creates ERPNext sales order, and confirms the order.
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

        // Push to ERPNext (SO + stock deduction)
        await PushOrderToERPNextAsync(order);

        HttpContext.Session.Remove(CartSessionKey);
        return RedirectToAction("Confirmation", new { orderNumber = order.OrderNumber });
    }

    /// <summary>
    /// Pushes a confirmed order to ERPNext: creates a Sales Order and immediately
    /// issues a Material Issue Stock Entry to deduct the sold quantities from the
    /// pickup store's warehouse (or the first active store for delivery orders).
    /// Failures are logged but never throw — order confirmation must not be blocked.
    /// </summary>
    private async Task PushOrderToERPNextAsync(Order order)
    {
        try
        {
            // Reload with items + store if not already included
            if (!order.Items.Any())
            {
                order = await _db.Orders
                    .Include(o => o.Items)
                    .Include(o => o.PickupStore)
                    .FirstAsync(o => o.Id == order.Id);
            }

            // Determine warehouse — pickup store takes priority, else first active store
            string warehouse;
            if (order.PickupStore?.ErpNextWarehouse is { Length: > 0 } pw)
            {
                warehouse = pw;
            }
            else
            {
                var firstStore = await _db.Stores.Where(s => s.IsActive).FirstOrDefaultAsync();
                warehouse = firstStore?.ErpNextWarehouse ?? "Sterlin Glams Abuja - SG";
            }

            // Resolve product → ERPNext item code
            var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
            var itemCodes = await _db.Products
                .Where(p => productIds.Contains(p.Id) && !string.IsNullOrEmpty(p.ErpNextItemCode))
                .ToDictionaryAsync(p => p.Id, p => p.ErpNextItemCode);

            var eligibleItems = order.Items
                .Where(i => itemCodes.ContainsKey(i.ProductId))
                .ToList();

            if (eligibleItems.Count == 0)
            {
                _logger.LogWarning("No ERPNext item codes found for order {OrderNumber}. Skipping ERPNext push.", order.OrderNumber);
                return;
            }

            // 1. Create + submit Sales Order (creates reservation in ERPNext)
            var customer = _config["ERPNext:DefaultCustomer"] ?? "Walk-In Customer";

            var soItems = eligibleItems.Select(i => new ERPNextSalesOrderItem
            {
                ItemCode  = itemCodes[i.ProductId],
                Warehouse = warehouse,
                Qty       = i.Quantity,
                Rate      = i.UnitPrice
            }).ToList();

            string soName;
            try
            {
                soName = await _erpNext.CreateSalesOrderAsync(new ERPNextCreateSalesOrderRequest
                {
                    Customer     = customer,
                    DeliveryDate = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd"),
                    Items        = soItems
                });
                await _erpNext.SubmitSalesOrderAsync(soName);
                order.ErpNextSalesOrderName = soName;
                await _db.SaveChangesAsync();
                _logger.LogInformation("ERPNext Sales Order {SoName} created for order {OrderNumber}", soName, order.OrderNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ERPNext Sales Order failed for order {OrderNumber}: {Message}", order.OrderNumber, ex.Message);
                soName = string.Empty;
            }

            // 2. Create + submit Material Issue to deduct actual stock
            var miItems = eligibleItems.Select(i => new ERPNextMaterialIssueItem
            {
                ItemCode       = itemCodes[i.ProductId],
                SourceWarehouse = warehouse,
                Qty            = i.Quantity,
                BasicRate      = i.UnitPrice
            }).ToList();

            var miName = await _erpNext.CreateMaterialIssueAsync(miItems, $"Web order {order.OrderNumber}");
            _logger.LogInformation("ERPNext Material Issue {MiName} created for order {OrderNumber} (warehouse: {Warehouse})",
                miName, order.OrderNumber, warehouse);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ERPNext push failed for order {OrderNumber}: {Message}", order.OrderNumber, ex.Message);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Confirmation(string orderNumber)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber && o.UserId == _userManager.GetUserId(User));

        if (order == null) return NotFound();

        return View(order);
    }

    private CartViewModel GetCart()
    {
        var json = HttpContext.Session.GetString(CartSessionKey);
        if (string.IsNullOrEmpty(json)) return new CartViewModel();
        return JsonSerializer.Deserialize<CartViewModel>(json) ?? new CartViewModel();
    }
}
