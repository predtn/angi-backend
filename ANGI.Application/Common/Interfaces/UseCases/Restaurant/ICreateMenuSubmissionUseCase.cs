using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Starts an editable menu draft for the authenticated owner's restaurant.</summary>
public interface ICreateMenuSubmissionUseCase
{
    /// <summary>Executes OWN-11.</summary>
    Task<MenuSubmissionWithItemsDto> ExecuteAsync(CancellationToken ct);
}
