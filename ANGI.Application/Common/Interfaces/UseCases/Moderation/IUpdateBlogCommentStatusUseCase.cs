using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>MOD-B3: Updates the moderation status of a blog comment.</summary>
public interface IUpdateBlogCommentStatusUseCase
{
    /// <summary>Executes the MOD-B3 use case.</summary>
    Task<UpdateBlogCommentStatusResponseDto> ExecuteAsync(long commentId, UpdateBlogCommentStatusRequestDto request, CancellationToken ct);
}
