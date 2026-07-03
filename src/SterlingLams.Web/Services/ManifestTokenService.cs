using Microsoft.AspNetCore.DataProtection;

namespace SterlingLams.Web.Services;

/// <summary>
/// Tamper-proof, unguessable token for a transfer manifest's public link — lets a driver/receiver
/// with no account open the manifest (like the store-pickup QR pass). No DB column needed: the
/// transfer id is data-protected into the token and read back out.
/// </summary>
public interface IManifestTokenService
{
    string Protect(int transferId);
    int? Unprotect(string token);
}

public class ManifestTokenService : IManifestTokenService
{
    private readonly IDataProtector _protector;
    public ManifestTokenService(IDataProtectionProvider dp) => _protector = dp.CreateProtector("Transfer.Manifest.v1");

    public string Protect(int transferId) => _protector.Protect(transferId.ToString());
    public int? Unprotect(string token)
    {
        try { return int.Parse(_protector.Unprotect(token)); } catch { return null; }
    }
}
