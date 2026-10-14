using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

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

    /// <summary>Locks and tracks the owner's restaurant while an OWN-06 submission is created.</summary>
    Task<ANGI.Domain.Entities.Restaurant?> LockOwnerRestaurantForVerificationAsync(int ownerId, CancellationToken ct);

    /// <summary>Adds a restaurant verification aggregate to the current unit of work.</summary>
    void AddVerification(RestaurantVerification verification);

    /// <summary>
    /// Reads the status of a verification only when it belongs to the restaurant; null when the id does not exist or
    /// belongs to another restaurant (OWN-08).
    /// </summary>
    Task<VerificationStatus?> GetVerificationStatusAsync(long restaurantId, long verificationId, CancellationToken ct);

    /// <summary>Reads the id of the restaurant's most recent verification, or null when it has none (OWN-08).</summary>
    Task<long?> GetLatestVerificationIdAsync(long restaurantId, CancellationToken ct);

    /// <summary>Loads and tracks only the owner's restaurant row, for changes to its own columns (OWN-05).</summary>
    Task<ANGI.Domain.Entities.Restaurant?> GetTrackedByOwnerIdAsync(int ownerId, CancellationToken ct);

    /// <summary>Loads and tracks only the owner's restaurant and its business hours so OWN-04 can replace them.</summary>
    Task<ANGI.Domain.Entities.Restaurant?> GetTrackedWithBusinessHoursAsync(int ownerId, CancellationToken ct);

    /// <summary>Retrieves a paginated list of restaurants matching search and filter criteria.</summary>
    Task<Paged<RestaurantCardDto>> BrowseAsync(BrowseRestaurantsRequestDto request, DateTimeOffset currentUtcTime, CancellationToken ct);

    /// <summary>Checks whether a restaurant exists and is publicly visible.</summary>
    Task<bool> ExistsAndActiveAsync(long restaurantId, CancellationToken ct);

    /// <summary>Loads public restaurant detail with images and business hours for REST-02.</summary>
    Task<Domain.Entities.Restaurant?> GetPublicDetailAsync(long restaurantId, CancellationToken ct);

    /// <summary>Loads the caller's review for a restaurant if one exists.</summary>
    Task<RestaurantReview?> GetTravelerReviewForRestaurantAsync(long restaurantId, int userId, CancellationToken ct);

    /// <summary>Loads active menu dishes for a restaurant, optionally filtered by tags and availability (REST-03).</summary>
    Task<IReadOnlyList<Dish>> GetActiveMenuDishesAsync(long restaurantId, IReadOnlyList<short>? tagIds, bool includeUnavailable, CancellationToken ct);

    /// <summary>Loads paginated visible reviews and rating statistics for a restaurant (REST-04).</summary>
    Task<(IReadOnlyList<RestaurantReview> Items, long TotalItems, IReadOnlyDictionary<string, int> RatingBreakdown, decimal RatingAvg, int RatingCount)> GetReviewsAsync(
        long restaurantId,
        short? rating,
        bool? hasImage,
        string? sort,
        int page,
        int pageSize,
        CancellationToken ct);
}
