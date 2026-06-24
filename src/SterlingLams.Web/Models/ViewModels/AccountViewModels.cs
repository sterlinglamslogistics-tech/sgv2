using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.ViewModels;

public class LoginViewModel
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Enter a valid email address")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

// ─── Two-factor authentication ────────────────────────────────────────────
public class TwoFactorLoginViewModel
{
    [Required(ErrorMessage = "Enter the code")]
    [Display(Name = "Authenticator code")]
    public string Code { get; set; } = string.Empty;

    public bool RememberMachine { get; set; }
    public bool UseRecoveryCode { get; set; }
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class TwoFactorSettingsViewModel
{
    public bool Is2faEnabled { get; set; }
    public int RecoveryCodesLeft { get; set; }
    /// <summary>Newline-separated recovery codes, shown once right after enabling/regenerating.</summary>
    public string? RecoveryCodes { get; set; }
}

public class TwoFactorSetupViewModel
{
    public string SharedKey { get; set; } = "";
    public string QrDataUri { get; set; } = "";

    [Required(ErrorMessage = "Enter the 6-digit code from your app")]
    [Display(Name = "Verification code")]
    public string Code { get; set; } = string.Empty;
}

public class RegisterViewModel
{
    [Required(ErrorMessage = "First name is required")]
    [StringLength(50)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required")]
    [StringLength(50)]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Enter a valid email address")]
    public string Email { get; set; } = string.Empty;

    [Phone]
    [Display(Name = "Phone number")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "Password is required")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please confirm your password")]
    [DataType(DataType.Password)]
    [Compare("Password", ErrorMessage = "Passwords do not match")]
    [Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class ProfileViewModel
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ActiveTab { get; set; } = "profile";
    public bool EmailConfirmed { get; set; }
    public int LoyaltyPoints { get; set; }
    public bool LoyaltyEnabled { get; set; }
    public int LoyaltyNairaPerPoint { get; set; } = 100;

    public List<OrderSummaryViewModel> RecentOrders { get; set; } = new();
    public List<SterlingLams.Web.Models.Domain.Address> Addresses { get; set; } = new();

    // Inline change-password form
    public ChangePasswordViewModel ChangePassword { get; set; } = new();
}

public class UpdateProfileViewModel
{
    [Required(ErrorMessage = "First name is required")]
    [StringLength(50)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required")]
    [StringLength(50)]
    public string LastName { get; set; } = string.Empty;

    [Phone]
    public string? Phone { get; set; }
}

public class OrderSummaryViewModel
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string FormattedTotal => $"₦{Total:N0}";
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int ItemCount { get; set; }
}

public class ChangePasswordViewModel
{
    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Compare("NewPassword", ErrorMessage = "Passwords do not match")]
    [Display(Name = "Confirm new password")]
    public string ConfirmNewPassword { get; set; } = string.Empty;
}

public class ResetPasswordViewModel
{
    [Required]
    public string Token { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please confirm your password")]
    [DataType(DataType.Password)]
    [Compare("NewPassword", ErrorMessage = "Passwords do not match")]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
