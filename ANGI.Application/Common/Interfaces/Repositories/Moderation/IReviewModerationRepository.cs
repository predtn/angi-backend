using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Provides review and reply moderation persistence reads and tracked writes.</summary>
public interface IReviewModerationRepository
{
    /// <summary>Retrieves reviews filtered by parameters for moderator inspection without tracking.</summary>
    Task<(IReadOnlyList<RestaurantReview> Items, long TotalCount)> GetModReviewsAsync(GetModReviewsRequestDto request, CancellationToken ct);

    /// <summary>Locks and tracks a review together with its restaurant aggregate for status updates.</summary>
    Task<RestaurantReview?> LockReviewWithRestaurantAsync(long reviewId, CancellationToken ct);

    /// <summary>Locks and tracks a review together with its reply for reply status updates.</summary>
    Task<RestaurantReview?> LockReviewWithReplyAsync(long reviewId, CancellationToken ct);

    /// <summary>Calculates the count and sum of ratings for all currently visible reviews of a restaurant.</summary>
    Task<(int Count, int RatingSum)> GetVisibleRatingStatsAsync(long restaurantId, CancellationToken ct);
}
