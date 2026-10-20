namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Defines the RM-05 operation for soft-deleting one traveler-owned roadmap.</summary>
public interface IDeleteRoadmapUseCase
{
    /// <summary>Soft-deletes the owned roadmap identified by the route value.</summary>
    Task ExecuteAsync(long roadmapId, CancellationToken ct);
}
