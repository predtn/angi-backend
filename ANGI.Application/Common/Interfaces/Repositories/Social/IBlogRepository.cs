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

    /// <summary>Loads a published, undeleted blog with its author, cover media, and ordered gallery images (BLOG-02).</summary>
    Task<Blog?> GetPublicDetailAsync(long blogId, CancellationToken ct);

    /// <summary>Determines whether the specified user has liked the given blog.</summary>
    Task<bool> IsLikedByUserAsync(long blogId, int userId, CancellationToken ct);

    /// <summary>Determines whether a published, undeleted blog with the given identifier exists.</summary>
    Task<bool> ExistsPublishedAsync(long blogId, CancellationToken ct);

    /// <summary>Loads a paginated list of visible, undeleted comments for a blog ordered oldest first (BLOG-03).</summary>
    Task<(IReadOnlyList<BlogComment> Items, long TotalItems)> GetVisibleCommentsAsync(
        long blogId,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>Adds a blog aggregate to the current unit of work.</summary>
    void Add(Blog blog);
}
