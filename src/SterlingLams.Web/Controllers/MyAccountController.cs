using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using QRCoder;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Each staff member's own back-office account: avatar, name, email, password, personal accent
/// colour, and two-factor. Reachable from every staff backend (Admin, Inventory, Marketing) via the
/// top-right user menu; rendered with the minimal back-office account layout, not the storefront.
/// </summary>
[Route("me")]
public class MyAccountController : Controller
{
    private static readonly HashSet<string> AllowedImg = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;

    public MyAccountController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
        IWebHostEnvironment env, IConfiguration config)
    {
        _users = users;
        _signIn = signIn;
        _env = env;
        _config = config;
    }

    // Any backend role (i.e. anything other than the storefront "Customer" role) counts as staff —
    // this covers Admin, Owner, Developer, the default staff roles, and any custom role.
    private async Task<bool> IsStaffAsync(ApplicationUser u) =>
        (await _users.GetRolesAsync(u)).Any(r => !string.Equals(r, "Customer", StringComparison.OrdinalIgnoreCase));

    // Gate the whole controller: anyone who isn't a signed-in staff member gets a plain 404 — the page
    // simply doesn't exist to outsiders (same as the secret-prefixed backends), rather than a login
    // redirect that would advertise a staff account area.
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = User.Identity?.IsAuthenticated == true ? await _users.GetUserAsync(User) : null;
        if (user == null || !await IsStaffAsync(user))
        {
            context.Result = NotFound();
            return;
        }
        await next();
    }

    // ── My Account page ────────────────────────────────────────────────────────
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var user = await _users.GetUserAsync(User); // guaranteed non-null staff by OnActionExecutionAsync
        if (user == null) return NotFound();
        ViewData["Title"] = "My Account";
        return View(await BuildVmAsync(user));
    }

    private async Task<MyAccountViewModel> BuildVmAsync(ApplicationUser user) => new()
    {
        FirstName        = user.FirstName,
        LastName         = user.LastName,
        Email            = user.Email ?? "",
        AvatarUrl        = user.AvatarUrl,
        Accent           = BackofficeChrome.IsHex(user.ThemeAccent) ? user.ThemeAccent! : "",
        Is2faEnabled     = await _users.GetTwoFactorEnabledAsync(user),
        RecoveryCodesLeft= await _users.CountRecoveryCodesAsync(user),
        RecoveryCodes    = TempData["RecoveryCodes"] as string
    };

    // ── Profile (name + email) ──────────────────────────────────────────────────
    [HttpPost("profile"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(string firstName, string lastName, string email)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        user.FirstName = (firstName ?? "").Trim();
        user.LastName  = (lastName ?? "").Trim();

        email = (email ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(email) &&
            !string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            var norm = _users.NormalizeEmail(email);
            var clash = _users.Users.Any(u => u.NormalizedEmail == norm && u.Id != user.Id);
            if (clash)
            {
                TempData["Error"] = "That email is already in use by another account.";
                return RedirectToAction(nameof(Index));
            }
            await _users.SetEmailAsync(user, email);
            await _users.SetUserNameAsync(user, email); // login is by email, keep them in sync
            user.EmailConfirmed = true;                 // staff self-service — trusted
        }

        await _users.UpdateAsync(user);
        await _signIn.RefreshSignInAsync(user);
        TempData["Success"] = "Your profile has been updated.";
        return RedirectToAction(nameof(Index));
    }

    // ── Avatar upload ───────────────────────────────────────────────────────────
    [HttpPost("avatar"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Avatar(IFormFile? file)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        if (file == null || file.Length == 0) { TempData["Error"] = "Choose an image first."; return RedirectToAction(nameof(Index)); }
        if (file.Length > 5 * 1024 * 1024) { TempData["Error"] = "Image too large (max 5 MB)."; return RedirectToAction(nameof(Index)); }
        var ext = Path.GetExtension(file.FileName);
        if (!AllowedImg.Contains(ext)) { TempData["Error"] = "Allowed types: JPG, PNG, WEBP, GIF."; return RedirectToAction(nameof(Index)); }

        string? url = await UploadAsync(file, ext);
        if (url == null) { TempData["Error"] = "Upload failed — please try again."; return RedirectToAction(nameof(Index)); }

        user.AvatarUrl = url;
        await _users.UpdateAsync(user);
        TempData["Success"] = "Profile picture updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("avatar/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAvatar()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        user.AvatarUrl = null;
        await _users.UpdateAsync(user);
        TempData["Success"] = "Profile picture removed.";
        return RedirectToAction(nameof(Index));
    }

    // Cloudinary when configured (persistent + CDN), else local disk (dev only). Returns null on failure.
    private async Task<string?> UploadAsync(IFormFile file, string ext)
    {
        var cloud = _config["Cloudinary:CloudName"]; var key = _config["Cloudinary:ApiKey"]; var secret = _config["Cloudinary:ApiSecret"];
        if (!string.IsNullOrWhiteSpace(cloud) && !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(secret))
        {
            try
            {
                var cloudinary = new Cloudinary(new Account(cloud, key, secret)) { Api = { Secure = true } };
                await using var s = file.OpenReadStream();
                var res = await cloudinary.UploadAsync(new ImageUploadParams
                {
                    File = new FileDescription(file.FileName, s),
                    Folder = "sterlinglams/avatars",
                    PublicId = Guid.NewGuid().ToString("N"),
                    UniqueFilename = false, Overwrite = false
                });
                return res.SecureUrl?.ToString();
            }
            catch { return null; }
        }
        try
        {
            var dir = Path.Combine(_env.WebRootPath, "uploads", "avatars");
            Directory.CreateDirectory(dir);
            var name = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
            await using var stream = System.IO.File.Create(Path.Combine(dir, name));
            await file.CopyToAsync(stream);
            return $"/uploads/avatars/{name}";
        }
        catch { return null; }
    }

    // ── Password ────────────────────────────────────────────────────────────────
    [HttpPost("password"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(string currentPassword, string newPassword, string confirmPassword)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        if (string.IsNullOrEmpty(newPassword) || newPassword != confirmPassword)
        {
            TempData["Error"] = "New passwords don't match.";
            return RedirectToAction(nameof(Index));
        }
        var result = await _users.ChangePasswordAsync(user, currentPassword ?? "", newPassword);
        if (!result.Succeeded)
        {
            TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
            return RedirectToAction(nameof(Index));
        }
        await _signIn.RefreshSignInAsync(user);
        TempData["Success"] = "Your password has been changed.";
        return RedirectToAction(nameof(Index));
    }

    // ── Theme (personal accent colour) ──────────────────────────────────────────
    [HttpPost("theme"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Theme(string? accent, bool reset = false)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        if (reset || string.IsNullOrWhiteSpace(accent))
            user.ThemeAccent = null;
        else if (BackofficeChrome.IsHex(accent.Trim()))
            user.ThemeAccent = accent.Trim().ToLowerInvariant();
        else
        {
            TempData["Error"] = "Pick a valid colour.";
            return RedirectToAction(nameof(Index));
        }

        await _users.UpdateAsync(user);
        TempData["Success"] = reset || user.ThemeAccent == null ? "Reset to the default colour." : "Your backend colour has been updated.";
        return RedirectToAction(nameof(Index));
    }

    // ── Two-factor authentication ───────────────────────────────────────────────
    [HttpGet("2fa/setup")]
    public async Task<IActionResult> SetupTwoFactor()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (await _users.GetTwoFactorEnabledAsync(user)) return RedirectToAction(nameof(Index));
        ViewData["Title"] = "Set up two-factor";
        return View(await BuildSetupVmAsync(user));
    }

    [HttpPost("2fa/enable"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableTwoFactor(string code)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        var clean = (code ?? "").Replace(" ", "").Replace("-", "");
        var valid = await _users.VerifyTwoFactorTokenAsync(
            user, _users.Options.Tokens.AuthenticatorTokenProvider, clean);
        if (!valid)
        {
            ModelState.AddModelError("code", "That code didn't match. Try the current one from your app.");
            var vm = await BuildSetupVmAsync(user);
            vm.Code = code ?? "";
            return View(nameof(SetupTwoFactor), vm);
        }

        await _users.SetTwoFactorEnabledAsync(user, true);
        var codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        TempData["RecoveryCodes"] = string.Join("\n", codes ?? Enumerable.Empty<string>());
        TempData["Success"] = "Two-factor authentication is on. Save your recovery codes below.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("2fa/disable"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableTwoFactor()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        await _users.SetTwoFactorEnabledAsync(user, false);
        await _users.ResetAuthenticatorKeyAsync(user);
        TempData["Success"] = "Two-factor authentication has been turned off.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("2fa/recovery"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateRecoveryCodes()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!await _users.GetTwoFactorEnabledAsync(user)) return RedirectToAction(nameof(Index));
        var codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        TempData["RecoveryCodes"] = string.Join("\n", codes ?? Enumerable.Empty<string>());
        TempData["Success"] = "New recovery codes generated. Save them — the old ones no longer work.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<TwoFactorSetupViewModel> BuildSetupVmAsync(ApplicationUser user)
    {
        var key = await _users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _users.ResetAuthenticatorKeyAsync(user);
            key = await _users.GetAuthenticatorKeyAsync(user);
        }
        var email = await _users.GetEmailAsync(user) ?? user.UserName ?? "user";
        const string issuer = "Sterlin Glams";
        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}"
                + $"?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        var spaced = string.Join(" ", System.Text.RegularExpressions.Regex.Matches(key ?? "", ".{1,4}").Select(m => m.Value));
        return new TwoFactorSetupViewModel
        {
            SharedKey = spaced,
            QrDataUri = "data:image/png;base64," + Convert.ToBase64String(png)
        };
    }
}

public class MyAccountViewModel
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string Accent { get; set; } = "";
    public bool Is2faEnabled { get; set; }
    public int RecoveryCodesLeft { get; set; }
    public string? RecoveryCodes { get; set; }
}
