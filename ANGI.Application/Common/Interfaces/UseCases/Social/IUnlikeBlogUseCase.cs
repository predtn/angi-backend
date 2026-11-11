using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Defines BLOG-09 to idempotently unlike a blog.</summary>
public interface IUnlikeBlogUseCase
{
    /// <summary>Removes like from the specified blog on behalf of the authenticated user.</summary>
    Task<BlogLikeResponseDto> ExecuteAsync(long blogId, CancellationToken ct);
}
