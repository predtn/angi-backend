namespace ANGI.Application.DTOs.Moderation;

/// <summary>Request body for MOD-F3 to change the visibility status of an owner reply.</summary>
public sealed class UpdateReviewReplyStatusRequestDto
{
    public string Status { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}
