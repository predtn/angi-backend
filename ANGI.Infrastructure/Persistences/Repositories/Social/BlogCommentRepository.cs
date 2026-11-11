using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Social;

/// <summary>Provides EF Core persistence operations for blog comments.</summary>
public sealed class BlogCommentRepository : IBlogCommentRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes comment persistence with the scoped database context.</summary>
    public BlogCommentRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public Task<BlogComment?> GetByIdAndBlogIdAsync(long commentId, long blogId, CancellationToken ct) =>
        _context.BlogComments
            .Include(comment => comment.Blog)
            .FirstOrDefaultAsync(comment => comment.Id == commentId && comment.BlogId == blogId, ct);

    /// <inheritdoc />
    public async Task<bool> TrySoftDeleteAsync(long commentId, long blogId, DateTime deletedAt, CancellationToken ct)
    {
        // The soft-delete filter skips deleted rows, so only one of two concurrent deletes updates the comment.
        var deleted = await _context.BlogComments
            .Where(comment => comment.Id == commentId && comment.BlogId == blogId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(comment => comment.DeletedAt, deletedAt)
                .SetProperty(comment => comment.UpdatedAt, deletedAt), ct) == 1;
        if (deleted)
        {
            await _context.Blogs
                .Where(blog => blog.Id == blogId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(blog => blog.CommentCount, blog => Math.Max(blog.CommentCount - 1, 0)), ct);
        }

        return deleted;
    }
}
