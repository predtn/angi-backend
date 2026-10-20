using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Roadmap;

/// <summary>Provides EF Core persistence operations for personal roadmap aggregates.</summary>
public sealed class RoadmapRepository : IRoadmapRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes the repository with the scoped application context.</summary>
    public RoadmapRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>Adds a roadmap and its child days to the current unit of work.</summary>
    public void Add(Domain.Entities.Roadmap roadmap)
    {
        _context.Roadmaps.Add(roadmap);
    }

    /// <summary>Locks the owned roadmap row and loads the graph required for update and response mapping.</summary>
    public async Task<Domain.Entities.Roadmap?> LockAndGetTrackedAsync(
        long roadmapId,
        int userId,
        CancellationToken ct)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($$"""
            SELECT 1
            FROM core.roadmaps
            WHERE id = {{roadmapId}}
              AND user_id = {{userId}}
              AND deleted_at IS NULL
            FOR UPDATE
            """, ct);

        return await _context.Roadmaps
            .IgnoreQueryFilters()
            .Where(roadmap => roadmap.Id == roadmapId &&
                              roadmap.UserId == userId &&
                              roadmap.DeletedAt == null)
            .Include(roadmap => roadmap.Days)
                .ThenInclude(day => day.Items)
                    .ThenInclude(item => item.Dish)
                        .ThenInclude(dish => dish.CoverMedia)
            .Include(roadmap => roadmap.Days)
                .ThenInclude(day => day.Items)
                    .ThenInclude(item => item.Dish)
                        .ThenInclude(dish => dish.Restaurant)
            .Include(roadmap => roadmap.Days)
                .ThenInclude(day => day.Items)
                    .ThenInclude(item => item.Dish)
                        .ThenInclude(dish => dish.DishTags)
                            .ThenInclude(dishTag => dishTag.Tag)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Projects the current user's feedback for only the requested roadmap items.</summary>
    public async Task<IReadOnlyDictionary<long, FeedbackValue>> GetFeedbackByItemIdsAsync(
        int userId,
        IReadOnlyCollection<long> roadmapItemIds,
        CancellationToken ct)
    {
        if (roadmapItemIds.Count == 0)
        {
            return new Dictionary<long, FeedbackValue>();
        }

        return await _context.DishFeedbacks
            .AsNoTracking()
            .Where(feedback => feedback.UserId == userId &&
                               feedback.RoadmapItemId.HasValue &&
                               roadmapItemIds.Contains(feedback.RoadmapItemId.Value))
            .ToDictionaryAsync(feedback => feedback.RoadmapItemId!.Value, feedback => feedback.Value, ct);
    }
}
