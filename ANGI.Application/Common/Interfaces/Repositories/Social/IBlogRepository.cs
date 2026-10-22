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

    /// <summary>Adds a blog aggregate to the current unit of work.</summary>
    void Add(Blog blog);

    /// <summary>Retrieves a blog by its ID, returning null if not found or soft-deleted.</summary>
    Task<Blog?> GetByIdAsync(long blogId, CancellationToken ct);

    /// <summary>Checks whether a user has liked a blog.</summary>
    Task<bool> HasUserLikedAsync(long blogId, int userId, CancellationToken ct);

    /// <summary>Retrieves an existing blog like by blog ID and user ID.</summary>
    Task<BlogLike?> GetLikeAsync(long blogId, int userId, CancellationToken ct);

    /// <summary>Adds a blog like.</summary>
    void AddLike(BlogLike like);

    /// <summary>Removes a blog like.</summary>
    void RemoveLike(BlogLike like);

    /// <summary>Adds a blog comment.</summary>
    void AddComment(BlogComment comment);
}
