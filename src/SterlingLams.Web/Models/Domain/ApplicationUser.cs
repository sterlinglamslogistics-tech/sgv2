using Microsoft.AspNetCore.Identity;

namespace SterlingLams.Web.Models.Domain;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Hashed PIN for quick till sign-in. Null = this user can't sign in at a till.</summary>
    public string? PinHash { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<WishlistItem> WishlistItems { get; set; } = new List<WishlistItem>();
    public ICollection<Address> Addresses { get; set; } = new List<Address>();
}
