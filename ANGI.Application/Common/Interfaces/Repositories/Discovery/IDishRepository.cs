using ANGI.Application.Common.Models.Discovery;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Discovery;

/// <summary>Defines the public dish reads of the Discovery module.</summary>
public interface IDishRepository
{
    /// <summary>
    /// Loads an active dish of a verified, visible, not deleted restaurant with its cover, tags, images and the
    /// restaurant's cover and business hours (DISH-02). Null when the dish does not exist or is not public.
    /// </summary>
    Task<Dish?> GetPublicDetailAsync(int dishId, CancellationToken ct);

    /// <summary>
    /// Returns the ids of at most <paramref name="maxCount"/> candidate dishes for reco, sorted by restaurant rating, then
    /// dish like_count, both descending (DISH-01, query A1).
    /// </summary>
    Task<IReadOnlyList<int>> GetCandidateIdsAsync(DishCandidateFilter filter, int maxCount, CancellationToken ct);

    /// <summary>
    /// Loads the dishes of a page with what a DishCard shows, keeping only those still candidates (a dish hidden or sold
    /// out since the list was ranked is left out). Order is not guaranteed.
    /// </summary>
    Task<IReadOnlyList<Dish>> GetCardDishesAsync(IReadOnlyCollection<int> dishIds, CancellationToken ct);
}
