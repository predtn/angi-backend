using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Provides persistence operations for blog and comment moderation.</summary>
public interface IBlogModerationRepository
{
    /// <summary>Lists blogs with optional search, status, author, and pending report filters.</summary>
    Task<Paged<ModBlogCardDto>> ListBlogsAsync(GetModBlogsRequestDto request, CancellationToken ct);

    /// <summary>Retrieves a blog by identifier with change tracking for moderation updates.</summary>
    Task<Blog?> GetBlogByIdAsync(long blogId, CancellationToken ct);

    /// <summary>Retrieves a blog comment by identifier with change tracking for moderation updates.</summary>
    Task<BlogComment?> GetCommentByIdAsync(long commentId, CancellationToken ct);

    /// <summary>Adds delta to blogs.comment_count in one atomic update (never below 0).</summary>
    Task AddToCommentCountAsync(long blogId, int delta, CancellationToken ct);

    /// <summary>Stages a notification for persistence in the current unit of work.</summary>
    void AddNotification(Domain.Entities.Notification notification);
}
