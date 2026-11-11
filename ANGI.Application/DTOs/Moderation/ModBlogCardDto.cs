using ANGI.Application.DTOs.Social;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>ModBlogCard of the API Design (MOD-B1): a blog in the moderation list.</summary>
public sealed class ModBlogCardDto : BlogCardDto
{
    /// <summary>Moderation status of the blog: published | hidden | removed.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Number of pending or in_review reports on this blog.</summary>
    public int PendingReportCount { get; init; }
}
