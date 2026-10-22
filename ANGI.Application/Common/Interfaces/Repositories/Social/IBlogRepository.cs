using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
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

    /// <summary>
    /// Retrieves a paged list of blogs authored by the specified user, optionally filtered by status,
    /// along with the latest moderation note for hidden or removed blogs and the total count (BLOG-05).
    /// </summary>
    Task<(IReadOnlyList<Blog> Items, Dictionary<long, string?> ModerationNotes, long TotalCount)> GetByAuthorAsync(
        int authorId,
        BlogStatus? status,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>Loads the current author and avatar for a newly created BlogDetail response.</summary>
    Task<User?> GetAuthorAsync(int userId, CancellationToken ct);

    /// <summary>Loads an owned, visible roadmap graph required to create an immutable share snapshot.</summary>
    Task<RoadmapSnapshotSource?> GetOwnedRoadmapForSnapshotAsync(long roadmapId, int userId, CancellationToken ct);

    /// <summary>Determines whether an undeleted blog already uses the supplied URL slug.</summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct);

    /// <summary>Determines whether an undeleted blog already uses the supplied URL slug, excluding an optional blog id.</summary>
    Task<bool> SlugExistsAsync(string slug, long? excludeBlogId, CancellationToken ct);

    /// <summary>Loads an undeleted blog aggregate including images and author with tracking for modification.</summary>
    Task<Blog?> GetTrackedByIdAsync(long blogId, CancellationToken ct);

    /// <summary>Adds a blog aggregate to the current unit of work.</summary>
    void Add(Blog blog);
}
