using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>MOD-F1: Moderation review list query.</summary>
public interface IGetModReviewsUseCase
{
    Task<Paged<ModReviewDto>> ExecuteAsync(GetModReviewsRequestDto request, CancellationToken ct);
}
