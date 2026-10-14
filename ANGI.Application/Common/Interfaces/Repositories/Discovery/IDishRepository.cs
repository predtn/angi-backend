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
}
