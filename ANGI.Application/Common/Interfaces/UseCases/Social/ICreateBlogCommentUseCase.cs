using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Defines BLOG-10 to add a comment to a blog.</summary>
public interface ICreateBlogCommentUseCase
{
    /// <summary>Creates a top-level comment on the blog.</summary>
    Task<CommentDto> ExecuteAsync(long blogId, CreateBlogCommentRequestDto request, CancellationToken ct);
}
