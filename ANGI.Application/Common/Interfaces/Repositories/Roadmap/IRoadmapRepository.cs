using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.Repositories.Roadmap;

/// <summary>Defines persistence operations required by roadmap planning use cases.</summary>
public interface IRoadmapRepository
{
    /// <summary>Returns one stable, newest-first page of non-deleted roadmaps owned by the supplied user.</summary>
    Task<Paged<RoadmapSummaryDto>> GetByUserAsync(int userId, int page, int pageSize, CancellationToken ct);

    /// <summary>Adds a new roadmap aggregate to the current unit of work.</summary>
    void Add(Domain.Entities.Roadmap roadmap);

    /// <summary>Locks and loads an owned roadmap aggregate for an atomic RM-04 update.</summary>
    Task<Domain.Entities.Roadmap?> LockAndGetTrackedAsync(long roadmapId, int userId, CancellationToken ct);

    /// <summary>Loads the caller's feedback values for roadmap items used by the detail mapper.</summary>
    Task<IReadOnlyDictionary<long, FeedbackValue>> GetFeedbackByItemIdsAsync(
        int userId,
        IReadOnlyCollection<long> roadmapItemIds,
        CancellationToken ct);
}
