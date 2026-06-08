namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A physical till / point-of-sale terminal, bound to a branch. The till device remembers
/// which Register it is, so every sale is tagged to the right branch automatically.
/// </summary>
public class Register
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;

    public bool IsActive { get; set; } = true;
}
