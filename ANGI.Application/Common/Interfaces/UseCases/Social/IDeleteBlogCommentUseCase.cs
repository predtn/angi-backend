namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Soft-deletes an owned blog comment and updates blog comment count (BLOG-11).</summary>
public interface IDeleteBlogCommentUseCase
{
    /// <summary>Verifies ownership and soft-deletes the comment atomically.</summary>
    Task ExecuteAsync(long blogId, long commentId, CancellationToken ct);
}
