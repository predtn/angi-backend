namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Defines the RM-09 operation for deleting one item from a traveler-owned roadmap.</summary>
public interface IDeleteRoadmapItemUseCase
{
    /// <summary>Deletes the item only when it belongs to the route roadmap and the current traveler.</summary>
    Task ExecuteAsync(long roadmapId, long itemId, CancellationToken ct);
}
