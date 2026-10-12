using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
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

    /// <summary>Retrieves a paginated list of restaurants matching search and filter criteria.</summary>
    Task<Paged<RestaurantCardDto>> BrowseAsync(BrowseRestaurantsRequestDto request, DateTimeOffset currentUtcTime, CancellationToken ct);
}
