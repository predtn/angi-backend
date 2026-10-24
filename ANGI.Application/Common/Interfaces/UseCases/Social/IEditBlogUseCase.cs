using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Edits a traveler-owned blog and updates its optional snapshot and media graph (BLOG-06).</summary>
public interface IEditBlogUseCase
{
    /// <summary>Validates, checks ownership, updates blog fields, media, or snapshot, and returns the updated blog detail.</summary>
    Task<BlogDetailDto> ExecuteAsync(long blogId, EditBlogRequestDto request, CancellationToken ct);
}
