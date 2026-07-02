using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services.Social;

/// <summary>
/// Publishes a scheduled post to its social channels. The default implementation is inert:
/// publishing to Instagram/Facebook/TikTok requires connecting those accounts (Meta Graph /
/// TikTok Content Posting APIs, which need app review + tokens). Swap in a real implementation
/// once approved; until then <see cref="IsEnabled"/> is false and the scheduler stays dormant.
/// </summary>
public interface ISocialPublisher
{
    /// <summary>True when at least one channel is connected and publishing is turned on.</summary>
    bool IsEnabled { get; }

    Task<(bool Ok, string? Error)> PublishAsync(SocialPost post, CancellationToken ct = default);
}

/// <summary>No accounts connected yet — records why instead of publishing.</summary>
public sealed class NullSocialPublisher : ISocialPublisher
{
    private readonly IConfiguration _config;
    public NullSocialPublisher(IConfiguration config) => _config = config;

    // Flip on only when the operator has both enabled it and connected an account.
    public bool IsEnabled => _config.GetValue("Social:Enabled", false);

    public Task<(bool Ok, string? Error)> PublishAsync(SocialPost post, CancellationToken ct = default)
        => Task.FromResult((false,
            (string?)"Publishing isn't connected yet. Link your Instagram/Facebook/TikTok accounts (pending Meta/TikTok app review) to enable it."));
}
