namespace ANGI.Application.DTOs.Moderation;

/// <summary>Request body for MOD-F2 to change the visibility status of a review.</summary>
public sealed class UpdateReviewStatusRequestDto
{
    public string Status { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}
