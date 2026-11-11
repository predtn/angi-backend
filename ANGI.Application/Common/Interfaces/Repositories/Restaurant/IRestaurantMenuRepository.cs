using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Interfaces.Repositories.Restaurant;

/// <summary>Defines persistence operations for owner menu management.</summary>
public interface IRestaurantMenuRepository
{
    /// <summary>Locks and loads the authenticated owner's restaurant for draft creation.</summary>
    Task<Domain.Entities.Restaurant?> LockOwnerRestaurantAsync(int ownerId, CancellationToken ct);

    /// <summary>Loads the owner restaurant with its visible menu and open submission.</summary>
    Task<Domain.Entities.Restaurant?> GetOwnerMenuAsync(int ownerId, CancellationToken ct);

    /// <summary>Loads and tracks a dish only when it belongs to the authenticated owner's restaurant.</summary>
    Task<Dish?> GetTrackedOwnerDishAsync(int ownerId, int dishId, CancellationToken ct);

    /// <summary>Loads the draft or pending submission for a restaurant.</summary>
    Task<MenuSubmission?> GetOpenSubmissionAsync(long restaurantId, CancellationToken ct);

    /// <summary>Loads all non-removed dishes used to seed a new draft.</summary>
    Task<IReadOnlyList<Dish>> GetDraftSourceDishesAsync(long restaurantId, CancellationToken ct);

    /// <summary>Adds a menu submission aggregate without saving immediately.</summary>
    void AddSubmission(MenuSubmission submission);

    /// <summary>Loads one page of submissions owned by the caller.</summary>
    Task<(IReadOnlyList<MenuSubmission> Items, long TotalItems)> GetSubmissionsAsync(
        int ownerId,
        MenuSubmissionStatus? status,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>Loads a submission with items, source dishes, media, and current menu for comparison.</summary>
    Task<MenuSubmission?> GetSubmissionDetailsAsync(int ownerId, long submissionId, CancellationToken ct);

    /// <summary>Loads and tracks a submission owned by the caller.</summary>
    Task<MenuSubmission?> GetTrackedSubmissionAsync(int ownerId, long submissionId, CancellationToken ct);

    /// <summary>Locks one owned submission until the surrounding transaction completes.</summary>
    Task<MenuSubmission?> LockOwnedSubmissionAsync(int ownerId, long submissionId, CancellationToken ct);

    /// <summary>Loads and tracks an item only through a submission owned by the caller.</summary>
    Task<MenuSubmissionItem?> GetTrackedItemAsync(
        int ownerId,
        long submissionId,
        long itemId,
        CancellationToken ct);

    /// <summary>Loads tag identifiers that currently exist.</summary>
    Task<IReadOnlyList<short>> GetExistingTagIdsAsync(
        IReadOnlyCollection<short> tagIds,
        CancellationToken ct);

    /// <summary>Adds a proposed menu item without saving immediately.</summary>
    void AddItem(MenuSubmissionItem item);

    /// <summary>Removes a proposed menu item without saving immediately.</summary>
    void RemoveItem(MenuSubmissionItem item);
}
