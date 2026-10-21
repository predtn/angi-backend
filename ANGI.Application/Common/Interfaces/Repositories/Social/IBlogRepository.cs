using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Social;

/// <summary>Reads public blogs.</summary>
public interface IBlogRepository
{
    /// <summary>
    /// Published, undeleted blogs for the home page (HOME-01): those published since <paramref name="recentSince"/> first,
    /// each group by like_count + comment_count, then newest; with the cover and the author's avatar.
    /// </summary>
    Task<IReadOnlyList<Blog>> GetFeaturedAsync(DateTime recentSince, int limit, CancellationToken ct);

    /// <summary>Checks whether an active blog slug already exists.</summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct);

    /// <summary>Adds a new blog entity to the change tracker.</summary>
    void Add(Blog blog);

    /// <summary>Loads an undeleted blog by id with author, cover, and image media navigations.</summary>
    Task<Blog?> GetByIdWithDetailsAsync(long id, CancellationToken ct);
}
