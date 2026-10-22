using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Creates a top-level comment on an active blog (BLOG-10).</summary>
public interface ICreateBlogCommentUseCase
{
    /// <summary>Validates, sanitizes, persists a new blog comment, and returns the created DTO.</summary>
    Task<CommentDto> ExecuteAsync(long blogId, CreateBlogCommentRequestDto request, CancellationToken ct);
}
