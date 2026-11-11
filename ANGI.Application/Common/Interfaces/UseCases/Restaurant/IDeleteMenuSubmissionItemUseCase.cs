namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Removes a dish proposal from an owner menu draft.</summary>
public interface IDeleteMenuSubmissionItemUseCase
{
    /// <summary>Executes OWN-16.</summary>
    Task ExecuteAsync(long submissionId, long itemId, CancellationToken ct);
}
