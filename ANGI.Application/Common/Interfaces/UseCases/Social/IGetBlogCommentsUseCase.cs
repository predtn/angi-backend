using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Returns a paginated list of visible comments for a published blog (BLOG-03).</summary>
public interface IGetBlogCommentsUseCase
{
    /// <summary>Retrieves visible comments ordered oldest first or throws 404 BLOG_NOT_FOUND when the blog does not exist.</summary>
    Task<Paged<CommentDto>> ExecuteAsync(long blogId, GetBlogCommentsRequestDto request, CancellationToken ct);
}
