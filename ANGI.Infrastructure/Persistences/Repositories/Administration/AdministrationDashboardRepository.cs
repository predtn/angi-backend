using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Administration;

/// <summary>Implements statistical and analytical dashboard queries against the core schema.</summary>
public sealed class AdministrationDashboardRepository : IAdministrationDashboardRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes the repository with the core persistence context.</summary>
    public AdministrationDashboardRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<DashboardSummaryDto> GetSummaryAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtcEnd = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // Users
        var totalUsers = await _context.Users.AsNoTracking().CountAsync(u => u.DeletedAt == null, ct);
        var newUsers = await _context.Users.AsNoTracking().CountAsync(u => u.DeletedAt == null && u.CreatedAt >= fromUtc && u.CreatedAt < toUtcEnd, ct);
        var userRoleCounts = await _context.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null)
            .GroupBy(u => u.Role.Code)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var userStatusCounts = await _context.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null)
            .GroupBy(u => u.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Restaurants
        var totalRestaurants = await _context.Restaurants.AsNoTracking().CountAsync(r => r.DeletedAt == null, ct);
        var newRestaurants = await _context.Restaurants.AsNoTracking().CountAsync(r => r.DeletedAt == null && r.CreatedAt >= fromUtc && r.CreatedAt < toUtcEnd, ct);
        var restVerifCounts = await _context.Restaurants.AsNoTracking()
            .Where(r => r.DeletedAt == null)
            .GroupBy(r => r.VerificationStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var restModCounts = await _context.Restaurants.AsNoTracking()
            .Where(r => r.DeletedAt == null)
            .GroupBy(r => r.ModerationStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Queues
        var pendingVerifications = await _context.RestaurantVerifications.AsNoTracking()
            .CountAsync(v => v.Status == VerificationStatus.Pending, ct);
        var pendingMenus = await _context.MenuSubmissions.AsNoTracking()
            .CountAsync(m => m.Status == MenuSubmissionStatus.Pending, ct);
        var pendingRestaurantReports = await _context.Reports.AsNoTracking()
            .CountAsync(r => r.TargetType == ReportTargetType.Restaurant && (r.Status == ReportStatus.Pending || r.Status == ReportStatus.InReview), ct);
        var pendingSocialReports = await _context.Reports.AsNoTracking()
            .CountAsync(r => r.TargetType == ReportTargetType.Blog && (r.Status == ReportStatus.Pending || r.Status == ReportStatus.InReview), ct);

        // Content
        var blogs = await _context.Blogs.AsNoTracking()
            .CountAsync(b => b.DeletedAt == null && b.CreatedAt >= fromUtc && b.CreatedAt < toUtcEnd, ct);
        var comments = await _context.BlogComments.AsNoTracking()
            .CountAsync(c => c.DeletedAt == null && c.CreatedAt >= fromUtc && c.CreatedAt < toUtcEnd, ct);
        var reviews = await _context.RestaurantReviews.AsNoTracking()
            .CountAsync(r => r.CreatedAt >= fromUtc && r.CreatedAt < toUtcEnd, ct);

        // Roadmaps
        var roadmapsCreated = await _context.Roadmaps.AsNoTracking()
            .CountAsync(r => r.DeletedAt == null && r.CreatedAt >= fromUtc && r.CreatedAt < toUtcEnd, ct);
        var aiGenerationJobs = await _context.RoadmapGenerationJobs.AsNoTracking()
            .CountAsync(j => j.CreatedAt >= fromUtc && j.CreatedAt < toUtcEnd, ct);

        // Feedback
        var likes = await _context.DishFeedbacks.AsNoTracking()
            .CountAsync(f => f.Value == FeedbackValue.Like && f.CreatedAt >= fromUtc && f.CreatedAt < toUtcEnd, ct);
        var dislikes = await _context.DishFeedbacks.AsNoTracking()
            .CountAsync(f => f.Value == FeedbackValue.Dislike && f.CreatedAt >= fromUtc && f.CreatedAt < toUtcEnd, ct);

        // Outbox
        var outboxPending = await _context.OutboxMessages.AsNoTracking()
            .CountAsync(o => o.Status == OutboxStatus.Pending, ct);
        var outboxDead = await _context.OutboxMessages.AsNoTracking()
            .CountAsync(o => o.Status == OutboxStatus.Dead, ct);

        return new DashboardSummaryDto
        {
            From = from,
            To = to,
            Users = new DashboardUsersSummaryDto
            {
                Total = totalUsers,
                New = newUsers,
                ByRole = userRoleCounts.ToDictionary(x => x.Role, x => x.Count),
                ByStatus = userStatusCounts.ToDictionary(x => ToSnakeCase(x.Status.ToString()), x => x.Count)
            },
            Restaurants = new DashboardRestaurantsSummaryDto
            {
                Total = totalRestaurants,
                New = newRestaurants,
                ByVerificationStatus = restVerifCounts.ToDictionary(x => ToSnakeCase(x.Status.ToString()), x => x.Count),
                ByModerationStatus = restModCounts.ToDictionary(x => ToSnakeCase(x.Status.ToString()), x => x.Count)
            },
            Queues = new DashboardQueuesSummaryDto
            {
                PendingVerifications = pendingVerifications,
                PendingMenus = pendingMenus,
                PendingRestaurantReports = pendingRestaurantReports,
                PendingSocialReports = pendingSocialReports
            },
            Content = new DashboardContentSummaryDto
            {
                Blogs = blogs,
                Comments = comments,
                Reviews = reviews
            },
            Roadmaps = new DashboardRoadmapsSummaryDto
            {
                Created = roadmapsCreated,
                AiGenerationJobs = aiGenerationJobs
            },
            Feedback = new DashboardFeedbackSummaryDto
            {
                Likes = likes,
                Dislikes = dislikes
            },
            Outbox = new DashboardOutboxSummaryDto
            {
                Pending = outboxPending,
                Dead = outboxDead
            }
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardTimeSeriesPointDto>> GetTimeSeriesAsync(
        string metric,
        DateOnly from,
        DateOnly to,
        string interval,
        CancellationToken ct)
    {
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtcEnd = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var buckets = GenerateBuckets(from, to, interval);
        var bucketCounts = buckets.ToDictionary(b => b, _ => 0);

        IQueryable<DateTime> createdDates = metric switch
        {
            "new_users" => _context.Users.AsNoTracking()
                .Where(x => x.DeletedAt == null && x.CreatedAt >= fromUtc && x.CreatedAt < toUtcEnd)
                .Select(x => x.CreatedAt),
            "new_restaurants" => _context.Restaurants.AsNoTracking()
                .Where(x => x.DeletedAt == null && x.CreatedAt >= fromUtc && x.CreatedAt < toUtcEnd)
                .Select(x => x.CreatedAt),
            "roadmaps" => _context.Roadmaps.AsNoTracking()
                .Where(x => x.DeletedAt == null && x.CreatedAt >= fromUtc && x.CreatedAt < toUtcEnd)
                .Select(x => x.CreatedAt),
            "blogs" => _context.Blogs.AsNoTracking()
                .Where(x => x.DeletedAt == null && x.CreatedAt >= fromUtc && x.CreatedAt < toUtcEnd)
                .Select(x => x.CreatedAt),
            "reports" => _context.Reports.AsNoTracking()
                .Where(x => x.CreatedAt >= fromUtc && x.CreatedAt < toUtcEnd)
                .Select(x => x.CreatedAt),
            "feedbacks" => _context.DishFeedbacks.AsNoTracking()
                .Where(x => x.CreatedAt >= fromUtc && x.CreatedAt < toUtcEnd)
                .Select(x => x.CreatedAt),
            "dish_rolls" => _context.DishRolls.AsNoTracking()
                .Where(x => x.RolledAt >= fromUtc && x.RolledAt < toUtcEnd)
                .Select(x => x.RolledAt),
            _ => throw new BadRequestException("INVALID_METRIC", "Metric không hợp lệ.")
        };

        var timestamps = await createdDates.ToListAsync(ct);

        foreach (var ts in timestamps)
        {
            var date = DateOnly.FromDateTime(ts);
            var bucket = MapToBucket(date, interval, buckets);
            if (bucket.HasValue && bucketCounts.ContainsKey(bucket.Value))
            {
                bucketCounts[bucket.Value]++;
            }
        }

        return buckets.Select(b => new DashboardTimeSeriesPointDto
        {
            Bucket = b,
            Value = bucketCounts[b]
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardTopItemDto>> GetTopAsync(string type, int limit, CancellationToken ct)
    {
        return type switch
        {
            "dishes" => await _context.Dishes.AsNoTracking()
                .Where(d => d.Status == DishStatus.Active)
                .OrderByDescending(d => d.LikeCount)
                .Take(limit)
                .Select(d => new DashboardTopItemDto
                {
                    Id = d.Id,
                    Name = d.Name,
                    Score = d.LikeCount,
                    Subtitle = d.Restaurant.Name
                })
                .ToListAsync(ct),

            "restaurants" => await _context.Restaurants.AsNoTracking()
                .Where(r => r.DeletedAt == null && r.ModerationStatus == RestaurantModerationStatus.Visible)
                .OrderByDescending(r => r.RatingAvg)
                .ThenByDescending(r => r.RatingCount)
                .Take(limit)
                .Select(r => new DashboardTopItemDto
                {
                    Id = r.Id,
                    Name = r.Name,
                    Score = r.RatingAvg,
                    Subtitle = null
                })
                .ToListAsync(ct),

            "blogs" => await _context.Blogs.AsNoTracking()
                .Where(b => b.DeletedAt == null && b.Status == BlogStatus.Published)
                .OrderByDescending(b => b.LikeCount)
                .Take(limit)
                .Select(b => new DashboardTopItemDto
                {
                    Id = b.Id,
                    Name = b.Title,
                    Score = b.LikeCount,
                    Subtitle = b.Author.DisplayName
                })
                .ToListAsync(ct),

            _ => throw new BadRequestException("INVALID_TOP_TYPE", "Category không hợp lệ.")
        };
    }

    private static List<DateOnly> GenerateBuckets(DateOnly from, DateOnly to, string interval)
    {
        var list = new List<DateOnly>();
        var current = from;

        if (interval == "month")
        {
            current = new DateOnly(from.Year, from.Month, 1);
            while (current <= to)
            {
                list.Add(current);
                current = current.AddMonths(1);
            }
        }
        else if (interval == "week")
        {
            while (current <= to)
            {
                list.Add(current);
                current = current.AddDays(7);
            }
        }
        else // day
        {
            while (current <= to)
            {
                list.Add(current);
                current = current.AddDays(1);
            }
        }

        return list;
    }

    private static DateOnly? MapToBucket(DateOnly date, string interval, List<DateOnly> buckets)
    {
        if (interval == "month")
        {
            return new DateOnly(date.Year, date.Month, 1);
        }

        if (interval == "week")
        {
            for (var i = buckets.Count - 1; i >= 0; i--)
            {
                if (date >= buckets[i])
                {
                    return buckets[i];
                }
            }
            return null;
        }

        return date;
    }

    private static string ToSnakeCase(string input)
    {
        return JsonNamingPolicy.SnakeCaseLower.ConvertName(input);
    }
}
