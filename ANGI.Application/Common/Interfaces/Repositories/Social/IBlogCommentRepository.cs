using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Social;

/// <summary>Defines blog comment persistence for BLOG-11.</summary>
public interface IBlogCommentRepository
{
    /// <summary>Retrieves a comment by id and blog id, including its associated blog aggregate.</summary>
    Task<BlogComment?> GetByIdAndBlogIdAsync(long commentId, long blogId, CancellationToken ct);

    /// <summary>
    /// Soft-deletes the comment if it is still visible and then decrements blogs.comment_count, in the caller's
    /// transaction; false when a concurrent call deleted it first, so the count drops once.
    /// </summary>
    Task<bool> TrySoftDeleteAsync(long commentId, long blogId, DateTime deletedAt, CancellationToken ct);
}
