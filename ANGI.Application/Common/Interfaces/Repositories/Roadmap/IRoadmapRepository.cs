using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Roadmap;

/// <summary>Defines roadmap persistence queries required by the application.</summary>
public interface IRoadmapRepository
{
    /// <summary>
    /// Loads an undeleted roadmap owned by <paramref name="userId"/> along with its days,
    /// items, dishes, and restaurants to create a blog snapshot.
    /// </summary>
    Task<Domain.Entities.Roadmap?> GetByIdForSnapshotAsync(long id, int userId, CancellationToken ct);
}
