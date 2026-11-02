namespace ANGI.Application.DTOs.Moderation;

/// <summary>Request body for updating blog comment status (MOD-B3).</summary>
public sealed class UpdateBlogCommentStatusRequestDto
{
    /// <summary>Target comment status: visible | hidden | removed.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Moderator's reason for the status update.</summary>
    public string Reason { get; init; } = string.Empty;
}
