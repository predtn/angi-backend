using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Partially updates a dish proposal in an owner menu draft.</summary>
public interface IUpdateMenuSubmissionItemUseCase
{
    /// <summary>Executes OWN-15.</summary>
    Task<MenuSubmissionItemDto> ExecuteAsync(
        long submissionId,
        long itemId,
        UpdateMenuSubmissionItemRequestDto request,
        CancellationToken ct);
}
