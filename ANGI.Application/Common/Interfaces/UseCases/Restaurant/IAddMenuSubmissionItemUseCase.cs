using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Adds a new dish proposal to an owner menu draft.</summary>
public interface IAddMenuSubmissionItemUseCase
{
    /// <summary>Executes OWN-14.</summary>
    Task<MenuSubmissionItemDto> ExecuteAsync(
        long submissionId,
        AddMenuSubmissionItemRequestDto request,
        CancellationToken ct);
}
