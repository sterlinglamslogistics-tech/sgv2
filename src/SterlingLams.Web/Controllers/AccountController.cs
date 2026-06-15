using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Controllers;

public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AccountController> _logger;
    private readonly SterlingLams.Web.Services.IEmailService _email;
    private readonly IWebHostEnvironment _env;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ApplicationDbContext db,
        ILogger<AccountController> logger,
        SterlingLams.Web.Services.IEmailService email,
        IWebHostEnvironment env)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
        _logger = logger;
        _email = email;
        _env = env;
    }

    // ─── Login ───────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToLocal(returnUrl);

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null)
            {
                user.LastLoginAt = DateTime.UtcNow;
                await _userManager.UpdateAsync(user);
            }
            _logger.LogInformation("User {Email} logged in", model.Email);

            if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                return Redirect(model.ReturnUrl);

            // Inventory-team staff land straight in the dedicated Inventory System.
            if (user != null
                && await _userManager.IsInRoleAsync(user, "Inventory")
                && !await _userManager.IsInRoleAsync(user, "Admin"))
                return RedirectToAction("Index", "Overview", new { area = "Inventory" });

            return RedirectToLocal(model.ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError("", "Too many failed attempts — your account is locked for 15 minutes. Try again later or reset your password.");
            return View(model);
        }

        ModelState.AddModelError("", "Invalid email or password.");
        return View(model);
    }

    // ─── Register ────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Profile");

        return View(new RegisterViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            FirstName = model.FirstName,
            LastName = model.LastName,
            PhoneNumber = model.Phone,
            CreatedAt = DateTime.UtcNow
        };

        var result = await _userManager.CreateAsync(user, model.Password);

        if (result.Succeeded)
        {
            _logger.LogInformation("New account created: {Email}", model.Email);
            await SendConfirmationEmailAsync(user);
            // Email confirmation is not enforced yet, so we still sign the user in — the link just
            // verifies their address for when enforcement is turned on.
            await _signInManager.SignInAsync(user, isPersistent: false);
            TempData["Success"] = $"Welcome, {user.FirstName}! We've sent a link to {user.Email} to confirm your email.";
            return RedirectToAction("Profile");
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError("", error.Description);

        return View(model);
    }

    // ─── Email confirmation ────────────────────────────────────────────────────

    private async Task SendConfirmationEmailAsync(ApplicationUser user)
    {
        if (string.IsNullOrWhiteSpace(user.Email)) return;
        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        var confirmLink = Url.Action(nameof(ConfirmEmail), "Account",
            new { userId = user.Id, token }, protocol: Request.Scheme)!;

        // Dev aid: when SMTP isn't configured the email is only logged, so surface the link.
        if (_env.IsDevelopment())
            _logger.LogInformation("[DEV] Email confirmation link for {Email}: {Link}", user.Email, confirmLink);

        var body = $@"
            <h2 style=""font-size:18px;margin:0 0 16px;"">Confirm your email</h2>
            <p>Thanks for creating an account with Sterlin Glams. Please confirm this is your email address by clicking below.</p>
            <p style=""margin:28px 0;"">
                <a href=""{confirmLink}"" style=""background:#0a0a0a;color:#ffffff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Confirm Email</a>
            </p>
            <p style=""font-size:13px;color:#78716c;"">If you didn't create this account, you can safely ignore this email.</p>";
        await _email.SendAsync(user.Email, "Confirm your email", body, $"{user.FirstName} {user.LastName}".Trim());
    }

    [HttpGet]
    public async Task<IActionResult> ConfirmEmail(string? userId, string? token)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
            return RedirectToAction(nameof(Login));

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            TempData["Error"] = "That confirmation link is no longer valid.";
            return RedirectToAction(nameof(Login));
        }

        if (user.EmailConfirmed)
        {
            TempData["Success"] = "Your email is already confirmed.";
            return RedirectToAction(User.Identity?.IsAuthenticated == true ? nameof(Profile) : nameof(Login));
        }

        var result = await _userManager.ConfirmEmailAsync(user, token);
        if (result.Succeeded)
        {
            _logger.LogInformation("Email confirmed for {Email}", user.Email);
            TempData["Success"] = "Thank you — your email is now confirmed.";
        }
        else
        {
            TempData["Error"] = "We couldn't confirm your email — the link may have expired. Please request a new one.";
        }
        return RedirectToAction(User.Identity?.IsAuthenticated == true ? nameof(Profile) : nameof(Login));
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> ResendConfirmation()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        if (user.EmailConfirmed)
            TempData["Success"] = "Your email is already confirmed.";
        else
        {
            await SendConfirmationEmailAsync(user);
            TempData["Success"] = $"Confirmation link sent to {user.Email}.";
        }
        return RedirectToAction(nameof(Profile));
    }

    // ─── Logout ──────────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    // ─── Profile ─────────────────────────────────────────────────────────────

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Profile(string tab = "profile")
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var orders = await _db.Orders
            .Where(o => o.UserId == user.Id)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new OrderSummaryViewModel
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                Total = o.Total,
                Status = o.Status.ToString(),
                CreatedAt = o.CreatedAt,
                ItemCount = o.Items.Count
            })
            .ToListAsync();

        var addresses = await _db.Addresses
            .Where(a => a.UserId == user.Id)
            .ToListAsync();

        var vm = new ProfileViewModel
        {
            FirstName   = user.FirstName,
            LastName    = user.LastName,
            Email       = user.Email ?? string.Empty,
            Phone       = user.PhoneNumber,
            CreatedAt   = user.CreatedAt,
            ActiveTab   = tab,
            EmailConfirmed = user.EmailConfirmed,
            RecentOrders = orders,
            Addresses   = addresses
        };

        return View(vm);
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProfile(UpdateProfileViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Please correct the form errors.";
            return RedirectToAction(nameof(Profile), new { tab = "profile" });
        }

        user.FirstName   = model.FirstName.Trim();
        user.LastName    = model.LastName.Trim();
        user.PhoneNumber = model.Phone?.Trim();
        await _userManager.UpdateAsync(user);

        TempData["Success"] = "Profile updated successfully.";
        return RedirectToAction(nameof(Profile), new { tab = "profile" });
    }

    // ─── Orders List ─────────────────────────────────────────────────────────

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Orders()
    {
        var userId = _userManager.GetUserId(User)!;

        var orders = await _db.Orders
            .Include(o => o.Items)
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return View(orders);
    }

    // ─── Order Detail ────────────────────────────────────────────────────────

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> OrderDetail(int id)
    {
        var userId = _userManager.GetUserId(User)!;

        var order = await _db.Orders
            .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p.Images)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId);

        if (order == null) return NotFound();

        return View(order);
    }

    // ─── Change Password ─────────────────────────────────────────────────────

    [Authorize]
    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);

        if (result.Succeeded)
        {
            await _signInManager.RefreshSignInAsync(user);
            TempData["Success"] = "Password updated successfully.";
            return RedirectToAction(nameof(Profile), new { tab = "security" });
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError("", error.Description);

        return View(model);
    }

    // ─── Forgot Password ─────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            ModelState.AddModelError("", "Please enter your email address.");
            return View();
        }

        var user = await _userManager.FindByEmailAsync(email);

        // Always show the confirmation view — don't reveal whether email exists
        if (user != null)
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var resetLink = Url.Action(nameof(ResetPassword), "Account",
                new { token, email = user.Email }, protocol: Request.Scheme)!;
            _logger.LogInformation("Password reset requested for {Email}.", email);

            var body = $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Reset your password</h2>
                <p>We received a request to reset the password for your account. Click the button below to choose a new one. This link expires shortly.</p>
                <p style=""margin:28px 0;"">
                    <a href=""{resetLink}"" style=""background:#0a0a0a;color:#ffffff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Reset Password</a>
                </p>
                <p style=""font-size:13px;color:#78716c;"">If you didn't request this, you can safely ignore this email — your password won't change.</p>";
            await _email.SendAsync(user.Email!, "Reset your password", body);
        }

        TempData["ForgotPasswordSent"] = true;
        return RedirectToAction(nameof(ForgotPasswordConfirmation));
    }

    [HttpGet]
    public IActionResult ForgotPasswordConfirmation() => View();

    // ─── Reset Password ──────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult ResetPassword(string? token, string? email)
    {
        if (token == null || email == null)
            return RedirectToAction(nameof(Login));

        return View(new ResetPasswordViewModel { Token = token, Email = email });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user == null)
        {
            // Don't reveal user existence — show success anyway
            return RedirectToAction(nameof(ResetPasswordConfirmation));
        }

        var result = await _userManager.ResetPasswordAsync(user, model.Token, model.NewPassword);
        if (result.Succeeded)
        {
            _logger.LogInformation("Password reset succeeded for {Email}", model.Email);
            return RedirectToAction(nameof(ResetPasswordConfirmation));
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError("", error.Description);

        return View(model);
    }

    [HttpGet]
    public IActionResult ResetPasswordConfirmation() => View();

    // ─── Access Denied ───────────────────────────────────────────────────────

    public IActionResult AccessDenied() => View();

    // ─── Helper ──────────────────────────────────────────────────────────────

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }
}
