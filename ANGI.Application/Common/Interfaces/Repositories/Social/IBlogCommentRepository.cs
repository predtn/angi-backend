using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Social;

/// <summary>Defines blog comment persistence and queries for the Social module.</summary>
public interface IBlogCommentRepository
{
    /// <summary>Retrieves an active, published blog by id.</summary>
    Task<Blog?> GetActiveBlogByIdAsync(long blogId, CancellationToken ct);

    /// <summary>Retrieves user information and avatar for mapping a comment author.</summary>
    Task<User?> GetUserAsync(int userId, CancellationToken ct);

    /// <summary>Retrieves a comment by id and blog id, including its associated blog aggregate.</summary>
    Task<BlogComment?> GetByIdAndBlogIdAsync(long commentId, long blogId, CancellationToken ct);

    /// <summary>Adds a comment entity to the current unit of work.</summary>
    void Add(BlogComment comment);

    /// <summary>Adds a notification entity to the current unit of work.</summary>
    void AddNotification(ANGI.Domain.Entities.Notification notification);
}
