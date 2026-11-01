using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>MOD-B2: Updates the moderation status of a blog.</summary>
public interface IUpdateBlogStatusUseCase
{
    /// <summary>Executes the MOD-B2 use case.</summary>
    Task<UpdateBlogStatusResponseDto> ExecuteAsync(long blogId, UpdateBlogStatusRequestDto request, CancellationToken ct);
}
