namespace ANGI.Application.DTOs.Moderation;

/// <summary>Response data for updating blog status (MOD-B2).</summary>
public sealed class UpdateBlogStatusResponseDto
{
    /// <summary>Identifier of the updated blog.</summary>
    public long BlogId { get; init; }

    /// <summary>Status of the blog after persistence: published | hidden | removed.</summary>
    public string Status { get; init; } = string.Empty;
}
