using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Defines BLOG-08 to idempotently like a blog.</summary>
public interface ILikeBlogUseCase
{
    /// <summary>Likes the specified blog on behalf of the authenticated user.</summary>
    Task<BlogLikeResponseDto> ExecuteAsync(long blogId, CancellationToken ct);
}
