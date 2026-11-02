namespace ANGI.Application.DTOs.Moderation;

/// <summary>Response body for MOD-F2 containing the updated review identifier and status.</summary>
public sealed class UpdateReviewStatusResponseDto
{
    public long ReviewId { get; init; }
    public string Status { get; init; } = string.Empty;
}
