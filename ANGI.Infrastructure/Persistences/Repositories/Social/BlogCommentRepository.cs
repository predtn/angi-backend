using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
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
    public Task<Blog?> GetActiveBlogByIdAsync(long blogId, CancellationToken ct) =>
        _context.Blogs
            .FirstOrDefaultAsync(blog => blog.Id == blogId && blog.Status == BlogStatus.Published, ct);

    /// <inheritdoc />
    public Task<User?> GetUserAsync(int userId, CancellationToken ct) =>
        _context.Users
            .AsNoTracking()
            .Include(user => user.AvatarMedia)
            .FirstOrDefaultAsync(user => user.Id == userId, ct);

    /// <inheritdoc />
    public Task<BlogComment?> GetByIdAndBlogIdAsync(long commentId, long blogId, CancellationToken ct) =>
        _context.BlogComments
            .Include(comment => comment.Blog)
            .FirstOrDefaultAsync(comment => comment.Id == commentId && comment.BlogId == blogId, ct);

    /// <inheritdoc />
    public void Add(BlogComment comment)
    {
        _context.BlogComments.Add(comment);
    }

    /// <inheritdoc />
    public void AddNotification(ANGI.Domain.Entities.Notification notification)
    {
        _context.Notifications.Add(notification);
    }
}
