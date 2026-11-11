using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Defines the BLOG-01 operation that lists public published blogs.</summary>
public interface IGetPublicBlogsUseCase
{
    /// <summary>Returns one validated page of published, non-deleted blogs matching the query filter.</summary>
    Task<Paged<BlogCardDto>> ExecuteAsync(GetPublicBlogsRequestDto request, CancellationToken ct);
}
