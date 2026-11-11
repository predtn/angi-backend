using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.Common.Interfaces.UseCases.Discovery;

/// <summary>Lists the tag catalogue (CAT-02).</summary>
public interface IGetTagsUseCase
{
    /// <summary>Tags sorted by type, then name.</summary>
    Task<IReadOnlyList<TagDto>> ExecuteAsync(GetTagsRequestDto request, CancellationToken ct);
}
