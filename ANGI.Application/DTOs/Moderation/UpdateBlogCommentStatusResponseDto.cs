namespace ANGI.Application.DTOs.Moderation;

/// <summary>Response data for updating blog comment status (MOD-B3).</summary>
public sealed class UpdateBlogCommentStatusResponseDto
{
    /// <summary>Identifier of the updated comment.</summary>
    public long CommentId { get; init; }

    /// <summary>Status of the comment after persistence: visible | hidden | removed.</summary>
    public string Status { get; init; } = string.Empty;
}
