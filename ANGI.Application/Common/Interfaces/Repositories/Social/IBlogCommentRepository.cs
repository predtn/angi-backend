using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Social;

/// <summary>Defines blog comment persistence for BLOG-11.</summary>
public interface IBlogCommentRepository
{
    /// <summary>Retrieves a comment by id and blog id, including its associated blog aggregate.</summary>
    Task<BlogComment?> GetByIdAndBlogIdAsync(long commentId, long blogId, CancellationToken ct);
}
