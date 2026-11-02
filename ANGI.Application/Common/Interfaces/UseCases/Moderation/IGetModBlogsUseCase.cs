using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>MOD-B1: Retrieves a paged list of blogs for moderation.</summary>
public interface IGetModBlogsUseCase
{
    /// <summary>Executes the MOD-B1 use case.</summary>
    Task<Paged<ModBlogCardDto>> ExecuteAsync(GetModBlogsRequestDto request, CancellationToken ct);
}
