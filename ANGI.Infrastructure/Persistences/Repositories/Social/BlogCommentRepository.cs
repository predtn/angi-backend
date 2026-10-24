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
}
