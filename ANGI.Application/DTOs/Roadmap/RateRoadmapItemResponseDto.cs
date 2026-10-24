namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Response data of RM-13.</summary>
public sealed class RateRoadmapItemResponseDto
{
    /// <summary>dish_feedbacks.id.</summary>
    public long FeedbackId { get; init; }

    /// <summary>like | dislike.</summary>
    public string Value { get; init; } = string.Empty;

    public int DishId { get; init; }

    /// <summary>dishes.like_count after this feedback.</summary>
    public int LikeCount { get; init; }

    /// <summary>dishes.dislike_count after this feedback.</summary>
    public int DislikeCount { get; init; }

    /// <summary>dish_feedbacks.created_at.</summary>
    public DateTime CreatedAt { get; init; }
}
