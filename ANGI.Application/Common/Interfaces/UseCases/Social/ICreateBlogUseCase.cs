using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Creates a new blog post and optional roadmap snapshot (BLOG-04).</summary>
public interface ICreateBlogUseCase
{
    /// <summary>Executes the write blog workflow.</summary>
    Task<BlogDetailDto> ExecuteAsync(CreateBlogRequestDto request, CancellationToken ct);
}
