using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Defines BLOG-04 creation of a published blog, optionally sharing a roadmap snapshot.</summary>
public interface ICreateBlogUseCase
{
    /// <summary>Creates a blog for the authenticated traveler and returns its complete detail.</summary>
    Task<BlogDetailDto> ExecuteAsync(CreateBlogRequestDto request, CancellationToken ct);
}
