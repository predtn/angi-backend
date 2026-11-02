namespace ANGI.Application.DTOs.Moderation;

/// <summary>Response body for MOD-F3 containing the review identifier and updated reply status.</summary>
public sealed class UpdateReviewReplyStatusResponseDto
{
    public long ReviewId { get; init; }
    public string ReplyStatus { get; init; } = string.Empty;
}
