namespace ANGI.Application.DTOs.Moderation;

/// <summary>Request body for updating blog status (MOD-B2).</summary>
public sealed class UpdateBlogStatusRequestDto
{
    /// <summary>Target blog status: published | hidden | removed.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Moderator's reason for the status update.</summary>
    public string Reason { get; init; } = string.Empty;
}
