using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Social;

/// <summary>Reads public blogs.</summary>
public sealed class BlogRepository : IBlogRepository
{
    private readonly ANGIContext _context;

    public BlogRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Blog>> GetFeaturedAsync(DateTime recentSince, int limit, CancellationToken ct)
    {
        // The soft-delete filter hides deleted blogs and blogs of deleted authors.
        return await _context.Blogs
            .AsNoTracking()
            .Where(blog => blog.Status == BlogStatus.Published)
            .OrderByDescending(blog => blog.PublishedAt >= recentSince)
            .ThenByDescending(blog => blog.LikeCount + blog.CommentCount)
            .ThenByDescending(blog => blog.PublishedAt)
            .ThenByDescending(blog => blog.Id)
            .Take(limit)
            .Include(blog => blog.CoverMedia)
            .Include(blog => blog.Author)
                .ThenInclude(author => author.AvatarMedia)
            .ToListAsync(ct);
    }
}
