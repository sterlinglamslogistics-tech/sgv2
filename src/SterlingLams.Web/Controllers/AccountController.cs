using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;
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
    private readonly SterlingLams.Web.Services.ILoyaltyService _loyalty;
    private readonly SterlingLams.Web.Services.ISettingsService _settings;
    private readonly SterlingLams.Web.Services.IAuditService _audit;
    private readonly IWebHostEnvironment _env;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ApplicationDbContext db,
        ILogger<AccountController> logger,
        SterlingLams.Web.Services.IEmailService email,
        SterlingLams.Web.Services.ILoyaltyService loyalty,
        SterlingLams.Web.Services.ISettingsService settings,
        SterlingLams.Web.Services.IAuditService audit,
        IWebHostEnvironment env)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
        _logger = logger;
        _email = email;
        _loyalty = loyalty;
        _settings = settings;
        _audit = audit;
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

        // Password OK but the account has an authenticator enrolled — challenge for the 2FA code.
        if (result.RequiresTwoFactor)
            return RedirectToAction(nameof(TwoFactorLogin), new { returnUrl = model.ReturnUrl, rememberMe = model.RememberMe });

        if (result.Succeeded)
        {
            // Look up by user name (what just signed in) rather than by email: email isn't
            // guaranteed unique in Identity, and FindByEmailAsync does a SingleOrDefault that
            // throws ("Sequence contains more than one element") if two accounts share an email
            // (e.g. a POS guest shell created with an existing customer's email).
            var user = await _userManager.FindByNameAsync(model.Email)
                       ?? await _userManager.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == _userManager.NormalizeEmail(model.Email));

            // Optional: require a confirmed email before customers can sign in (admin-toggled).
            // Staff (any role) are exempt so enabling this can't lock out the back office.
            if (user != null && !user.EmailConfirmed
                && await _settings.GetBoolAsync("security.require_email_confirmation", false)
                && (await _userManager.GetRolesAsync(user)).Count == 0)
            {
                await _signInManager.SignOutAsync();
                ModelState.AddModelError(string.Empty,
                    "Please confirm your email address before signing in — check your inbox for the confirmation link.");
                return View(model);
            }

            _logger.LogInformation("User {Email} logged in", model.Email);
            return await FinishLoginAsync(user, model.ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError("", "Too many failed attempts — your account is locked for 15 minutes. Try again later or reset your password.");
            return View(model);
        }

        ModelState.AddModelError("", "Invalid email or password.");
        return View(model);
    }

    // Shared post-sign-in steps (stamp last-login, audit staff, route to the right home). Used by
    // both the password path and the 2FA path.
    private async Task<IActionResult> FinishLoginAsync(ApplicationUser? user, string? returnUrl)
    {
        if (user != null)
        {
            user.LastLoginAt = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);
            var roles = await _userManager.GetRolesAsync(user);
            if (roles.Count > 0)
                try { await _audit.LogAsync("Login", "Account", user.Id, $"{user.FullName} signed in ({string.Join(", ", roles)})"); }
                catch { /* auditing must never block login */ }
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        // Inventory-team staff land straight in the dedicated Inventory System.
        if (user != null
            && await _userManager.IsInRoleAsync(user, "Inventory")
            && !await _userManager.IsInRoleAsync(user, "Admin"))
            return RedirectToAction("Index", "Overview", new { area = "Inventory" });

        return RedirectToLocal(returnUrl);
    }

    // ─── Two-factor (TOTP authenticator) sign-in challenge ────────────────────
    [HttpGet]
    public async Task<IActionResult> TwoFactorLogin(string? returnUrl = null, bool rememberMe = false, bool useRecoveryCode = false)
    {
        // Must have a password-validated user pending 2FA (set by PasswordSignInAsync).
        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));
        return View(new TwoFactorLoginViewModel { ReturnUrl = returnUrl, RememberMe = rememberMe, UseRecoveryCode = useRecoveryCode });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> TwoFactorLogin(TwoFactorLoginViewModel model)
    {
        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));
        if (!ModelState.IsValid) return View(model);

        var code = (model.Code ?? "").Replace(" ", "").Replace("-", "").Trim();
        Microsoft.AspNetCore.Identity.SignInResult result;
        if (model.UseRecoveryCode)
            result = await _signInManager.TwoFactorRecoveryCodeSignInAsync(code);
        else
            result = await _signInManager.TwoFactorAuthenticatorSignInAsync(code, model.RememberMe, model.RememberMachine);

        if (result.Succeeded)
        {
            _logger.LogInformation("User {User} completed 2FA login", user.UserName);
            return await FinishLoginAsync(user, model.ReturnUrl);
        }
        if (result.IsLockedOut)
        {
            ModelState.AddModelError("", "Too many failed attempts — your account is locked for 15 minutes.");
            return View(model);
        }
        ModelState.AddModelError("", model.UseRecoveryCode ? "Invalid recovery code." : "Invalid authenticator code.");
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

        // If a guest shell already exists for this email, upgrade it into a real account so the
        // person keeps the orders they placed as a guest, instead of failing with "email taken".
        var existing = await _userManager.FindByEmailAsync(model.Email);
        if (existing != null && existing.IsGuest)
            return await UpgradeGuestAsync(existing, model);

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

    // Convert an existing guest shell (random password, IsGuest=true) into a real account, keeping
    // its order history. ResetPassword replaces the shell's random password with the chosen one.
    private async Task<IActionResult> UpgradeGuestAsync(ApplicationUser guest, RegisterViewModel model)
    {
        var token = await _userManager.GeneratePasswordResetTokenAsync(guest);
        var pwResult = await _userManager.ResetPasswordAsync(guest, token, model.Password);
        if (!pwResult.Succeeded)
        {
            foreach (var error in pwResult.Errors) ModelState.AddModelError("", error.Description);
            return View("Register", model);
        }

        guest.FirstName = model.FirstName;
        guest.LastName = model.LastName;
        guest.PhoneNumber = model.Phone;
        guest.IsGuest = false;
        await _userManager.UpdateAsync(guest);

        _logger.LogInformation("Guest account upgraded to a full account: {Email}", guest.Email);
        await SendConfirmationEmailAsync(guest);
        await _signInManager.SignInAsync(guest, isPersistent: false);
        TempData["Success"] = $"Welcome back, {guest.FirstName}! Your account is ready and your previous orders are now linked.";
        return RedirectToAction("Profile");
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

        var subject = await _settings.GetAsync("email.email_confirm.subject", "Confirm your email");
        var intro = await _settings.GetAsync("email.email_confirm.intro", "Thanks for creating an account with us. Please confirm this is your email address by clicking below.");
        var body = $@"
            <h2 style=""font-size:18px;margin:0 0 16px;"">Confirm your email</h2>
            <p>{System.Net.WebUtility.HtmlEncode(intro)}</p>
            <p style=""margin:28px 0;"">
                <a href=""{confirmLink}"" style=""background:#0a0a0a;color:#ffffff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Confirm Email</a>
            </p>
            <p style=""font-size:13px;color:#78716c;"">If you didn't create this account, you can safely ignore this email.</p>";
        await _email.SendAsync(user.Email, subject, body, $"{user.FirstName} {user.LastName}".Trim());
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
            LoyaltyPoints = await _loyalty.GetBalanceAsync(user.Id),
            LoyaltyEnabled = await _settings.GetBoolAsync("loyalty.enabled", true),
            LoyaltyNairaPerPoint = (int)await _settings.GetDecimalAsync("loyalty.naira_per_point", 100m),
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

    // ─── Saved Addresses ───────────────────────────────────────────────────────

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAddress(int id, string label, string fullName, string phone,
        string line1, string? line2, string city, string state, string? postalCode, bool isDefault = false)
    {
        var userId = _userManager.GetUserId(User)!;
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(phone)
            || string.IsNullOrWhiteSpace(line1) || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(state))
        {
            TempData["Error"] = "Please fill in name, phone, address line 1, city and state.";
            return RedirectToAction(nameof(Profile), new { tab = "addresses" });
        }

        var addr = id > 0
            ? await _db.Addresses.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId)
            : new Address { UserId = userId };
        if (addr == null) return NotFound();

        addr.Label = string.IsNullOrWhiteSpace(label) ? "Home" : label.Trim();
        addr.FullName = fullName.Trim();
        addr.Phone = phone.Trim();
        addr.Line1 = line1.Trim();
        addr.Line2 = string.IsNullOrWhiteSpace(line2) ? null : line2.Trim();
        addr.City = city.Trim();
        addr.State = state.Trim();
        addr.PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();

        if (id == 0)
        {
            // First address becomes the default automatically.
            if (!await _db.Addresses.AnyAsync(a => a.UserId == userId)) isDefault = true;
            _db.Addresses.Add(addr);
        }

        if (isDefault)
        {
            foreach (var other in await _db.Addresses.Where(a => a.UserId == userId && a.Id != addr.Id).ToListAsync())
                other.IsDefault = false;
            addr.IsDefault = true;
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "Address saved.";
        return RedirectToAction(nameof(Profile), new { tab = "addresses" });
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAddress(int id)
    {
        var userId = _userManager.GetUserId(User)!;
        var addr = await _db.Addresses.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (addr != null)
        {
            var wasDefault = addr.IsDefault;
            _db.Addresses.Remove(addr);
            await _db.SaveChangesAsync();
            if (wasDefault)
            {
                var next = await _db.Addresses.Where(a => a.UserId == userId).OrderBy(a => a.Id).FirstOrDefaultAsync();
                if (next != null) { next.IsDefault = true; await _db.SaveChangesAsync(); }
            }
            TempData["Success"] = "Address removed.";
        }
        return RedirectToAction(nameof(Profile), new { tab = "addresses" });
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefaultAddress(int id)
    {
        var userId = _userManager.GetUserId(User)!;
        var addr = await _db.Addresses.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (addr != null)
        {
            foreach (var other in await _db.Addresses.Where(a => a.UserId == userId).ToListAsync())
                other.IsDefault = other.Id == id;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Default address updated.";
        }
        return RedirectToAction(nameof(Profile), new { tab = "addresses" });
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

    // ─── Two-factor management (authenticator app) ───────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> Security()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var vm = new TwoFactorSettingsViewModel
        {
            Is2faEnabled = await _userManager.GetTwoFactorEnabledAsync(user),
            RecoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user),
            // Recovery codes are shown exactly once, right after enabling (carried in TempData).
            RecoveryCodes = TempData["RecoveryCodes"] as string
        };
        return View(vm);
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> SetupTwoFactor()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        if (await _userManager.GetTwoFactorEnabledAsync(user))
            return RedirectToAction(nameof(Security));

        var vm = await BuildSetupVmAsync(user);
        return View(vm);
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableTwoFactor(string code)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var clean = (code ?? "").Replace(" ", "").Replace("-", "").Trim();
        var valid = await _userManager.VerifyTwoFactorTokenAsync(
            user, _userManager.Options.Tokens.AuthenticatorTokenProvider, clean);
        if (!valid)
        {
            ModelState.AddModelError("code", "That code didn't match — check your app's clock and try the current code.");
            return View(nameof(SetupTwoFactor), await BuildSetupVmAsync(user));
        }

        await _userManager.SetTwoFactorEnabledAsync(user, true);
        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        TempData["RecoveryCodes"] = string.Join("\n", codes ?? Enumerable.Empty<string>());
        try { await _audit.LogAsync("Enable2FA", "Account", user.Id, $"{user.FullName} enabled two-factor authentication"); } catch { }
        TempData["Success"] = "Two-factor authentication is on. Save your recovery codes below.";
        return RedirectToAction(nameof(Security));
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableTwoFactor()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user); // invalidate the old secret
        try { await _audit.LogAsync("Disable2FA", "Account", user.Id, $"{user.FullName} disabled two-factor authentication"); } catch { }
        TempData["Success"] = "Two-factor authentication has been turned off.";
        return RedirectToAction(nameof(Security));
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateRecoveryCodes()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!await _userManager.GetTwoFactorEnabledAsync(user)) return RedirectToAction(nameof(Security));
        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        TempData["RecoveryCodes"] = string.Join("\n", codes ?? Enumerable.Empty<string>());
        try { await _audit.LogAsync("Update", "Account", user.Id, $"{user.FullName} regenerated 2FA recovery codes"); } catch { }
        TempData["Success"] = "New recovery codes generated — your old codes no longer work.";
        return RedirectToAction(nameof(Security));
    }

    // Builds the authenticator-setup view model: ensures a key exists, then renders the manual key,
    // the otpauth:// URI and an inline QR image (data URI) for scanning.
    private async Task<TwoFactorSetupViewModel> BuildSetupVmAsync(ApplicationUser user)
    {
        var key = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            key = await _userManager.GetAuthenticatorKeyAsync(user);
        }
        var email = await _userManager.GetEmailAsync(user) ?? user.UserName ?? "user";
        const string issuer = "Sterlin Glams";
        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}"
                + $"?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);

        // Group the key in 4s for easier manual entry.
        var spaced = string.Join(" ", System.Text.RegularExpressions.Regex.Matches(key ?? "", ".{1,4}").Select(m => m.Value));
        return new TwoFactorSetupViewModel
        {
            SharedKey = spaced,
            QrDataUri = "data:image/png;base64," + Convert.ToBase64String(png)
        };
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

            var subject = await _settings.GetAsync("email.password_reset.subject", "Reset your password");
            var intro = await _settings.GetAsync("email.password_reset.intro", "We received a request to reset your password. Click below to choose a new one. This link expires shortly.");
            var body = $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Reset your password</h2>
                <p>{System.Net.WebUtility.HtmlEncode(intro)}</p>
                <p style=""margin:28px 0;"">
                    <a href=""{resetLink}"" style=""background:#0a0a0a;color:#ffffff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Reset Password</a>
                </p>
                <p style=""font-size:13px;color:#78716c;"">If you didn't request this, you can safely ignore this email — your password won't change.</p>";
            await _email.SendAsync(user.Email!, subject, body);
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
