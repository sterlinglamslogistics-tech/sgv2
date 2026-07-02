using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.Domain;

/// <summary>Social channels a post targets (bit flags).</summary>
[Flags]
public enum SocialChannel
{
    None = 0,
    Instagram = 1,
    Facebook = 2,
    TikTok = 4
}

public enum SocialPostStatus
{
    Draft,
    Scheduled,
    Published,
    Failed
}

/// <summary>
/// A scheduled social-media post (content calendar). Composing + scheduling works today; actual
/// publishing to Instagram/Facebook/TikTok is gated on connecting those accounts (Meta/TikTok app
/// review) — see SocialPublisherService, dormant until Social:Enabled + credentials are set.
/// </summary>
public class SocialPost
{
    public int Id { get; set; }

    [MaxLength(3000)]
    public string Content { get; set; } = string.Empty;
    [MaxLength(500)]
    public string? ImageUrl { get; set; }

    public SocialChannel Channels { get; set; } = SocialChannel.None;

    public SocialPostStatus Status { get; set; } = SocialPostStatus.Draft;
    /// <summary>When to publish (UTC).</summary>
    public DateTime? ScheduledAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    [MaxLength(400)]
    public string? Error { get; set; }

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
