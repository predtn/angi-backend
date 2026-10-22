using ANGI.Domain.Entities;
using ANGI.Application.Common.Models.Social;

namespace ANGI.Application.Common.Interfaces.Repositories.Social;

/// <summary>Defines blog persistence and the read models needed by Social use cases.</summary>
public interface IBlogRepository
{
    /// <summary>
    /// Published, undeleted blogs for the home page (HOME-01): those published since <paramref name="recentSince"/> first,
    /// each group by like_count + comment_count, then newest; with the cover and the author's avatar.
    /// </summary>
    Task<IReadOnlyList<Blog>> GetFeaturedAsync(DateTime recentSince, int limit, CancellationToken ct);

    /// <summary>Loads the current author and avatar for a newly created BlogDetail response.</summary>
    Task<User?> GetAuthorAsync(int userId, CancellationToken ct);

    /// <summary>Loads an owned, visible roadmap graph required to create an immutable share snapshot.</summary>
    Task<RoadmapSnapshotSource?> GetOwnedRoadmapForSnapshotAsync(long roadmapId, int userId, CancellationToken ct);

    /// <summary>Determines whether an undeleted blog already uses the supplied URL slug.</summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct);

    /// <summary>Loads an undeleted blog tracked for modification or soft deletion.</summary>
    Task<Blog?> GetTrackedByIdAsync(long blogId, CancellationToken ct);

    /// <summary>Adds a blog aggregate to the current unit of work.</summary>
    void Add(Blog blog);
}
