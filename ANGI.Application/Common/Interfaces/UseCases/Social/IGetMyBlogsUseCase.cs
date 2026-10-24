using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Defines the BLOG-05 operation that lists blogs authored by the authenticated traveler.</summary>
public interface IGetMyBlogsUseCase
{
    /// <summary>Returns one validated page of blogs authored by the current traveler, with moderation details.</summary>
    Task<Paged<MyBlogCardDto>> ExecuteAsync(GetMyBlogsRequestDto request, CancellationToken ct);
}
