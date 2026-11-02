using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Moderation;

/// <summary>Implements review moderation reads and aggregate locks for MOD-F1, MOD-F2 and MOD-F3.</summary>
public sealed class ReviewModerationRepository(ANGIContext context) : IReviewModerationRepository
{
    public async Task<(IReadOnlyList<RestaurantReview> Items, long TotalCount)> GetModReviewsAsync(
        GetModReviewsRequestDto request,
        CancellationToken ct)
    {
        var query = context.RestaurantReviews
            .AsNoTracking()
            .Include(r => r.Restaurant)
            .Include(r => r.User).ThenInclude(u => u.AvatarMedia)
            .Include(r => r.Images).ThenInclude(i => i.Media)
            .Include(r => r.Reply)
            .AsSplitQuery()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var term = $"%{request.Q.Trim()}%";
            query = query.Where(r => (r.Content != null && EF.Functions.ILike(r.Content, term))
                                     || EF.Functions.ILike(r.Restaurant.Name, term));
        }

        if (request.RestaurantId.HasValue)
        {
            query = query.Where(r => r.RestaurantId == request.RestaurantId.Value);
        }

        if (request.UserId.HasValue)
        {
            query = query.Where(r => r.UserId == request.UserId.Value);
        }

        if (request.Rating.HasValue)
        {
            query = query.Where(r => r.Rating == request.Rating.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Status) &&
            Enum.TryParse<ContentStatus>(request.Status, ignoreCase: true, out var statusEnum))
        {
            query = query.Where(r => r.Status == statusEnum);
        }

        var totalCount = await query.LongCountAsync(ct);

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<RestaurantReview?> LockReviewWithRestaurantAsync(long reviewId, CancellationToken ct)
    {
        return await context.RestaurantReviews
            .Include(r => r.Restaurant)
            .FirstOrDefaultAsync(r => r.Id == reviewId, ct);
    }

    public async Task<RestaurantReview?> LockReviewWithReplyAsync(long reviewId, CancellationToken ct)
    {
        return await context.RestaurantReviews
            .Include(r => r.Reply)
            .FirstOrDefaultAsync(r => r.Id == reviewId, ct);
    }

    public async Task<(int Count, int RatingSum)> GetVisibleRatingStatsAsync(long restaurantId, CancellationToken ct)
    {
        var stats = await context.RestaurantReviews
            .AsNoTracking()
            .Where(r => r.RestaurantId == restaurantId && r.Status == ContentStatus.Visible)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), RatingSum = g.Sum(r => (int)r.Rating) })
            .SingleOrDefaultAsync(ct);

        return stats is null ? (0, 0) : (stats.Count, stats.RatingSum);
    }
}
