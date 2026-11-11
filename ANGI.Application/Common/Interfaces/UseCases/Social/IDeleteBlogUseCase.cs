namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Defines the BLOG-07 operation for soft-deleting one traveler-owned blog.</summary>
public interface IDeleteBlogUseCase
{
    /// <summary>Soft-deletes the owned blog identified by the route value.</summary>
    Task ExecuteAsync(long blogId, CancellationToken ct);
}
