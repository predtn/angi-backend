using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Returns the full detail of a published blog, including author and roadmap snapshot (BLOG-02).</summary>
public interface IGetBlogDetailUseCase
{
    /// <summary>Retrieves a published blog by identifier or throws 404 BLOG_NOT_FOUND.</summary>
    Task<BlogDetailDto> ExecuteAsync(long blogId, CancellationToken ct);
}
