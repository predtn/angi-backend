using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Restaurant;

/// <summary>Defines persistence operations required by restaurant use cases.</summary>
public interface IRestaurantRepository
{
    /// <summary>Locks the owner row for the current transaction so restaurant registration is serialized per owner.</summary>
    Task LockOwnerForRegistrationAsync(int ownerId, CancellationToken ct);

    /// <summary>Checks whether the owner already has a non-deleted restaurant.</summary>
    Task<bool> ExistsByOwnerIdAsync(int ownerId, CancellationToken ct);

    /// <summary>Checks whether a non-deleted restaurant already uses the supplied slug.</summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct);

    /// <summary>Adds a restaurant aggregate to the current unit of work.</summary>
    void Add(ANGI.Domain.Entities.Restaurant restaurant);

    /// <summary>Loads the complete owner profile without tracking for OWN-02 responses.</summary>
    Task<ANGI.Domain.Entities.Restaurant?> GetOwnerProfileAsync(int ownerId, CancellationToken ct);

    /// <summary>Loads and tracks the complete owner profile so OWN-03 can update it.</summary>
    Task<ANGI.Domain.Entities.Restaurant?> GetTrackedOwnerProfileAsync(int ownerId, CancellationToken ct);
}
