using ANGI.Application.Common.Models.Discovery;

namespace ANGI.Application.Common.Interfaces.Services.Discovery;

/// <summary>Keeps DISH-01 ranked lists for 30 minutes so later pages reuse the first reco call (API Design, DISH-01).</summary>
public interface IRankedDishListCache
{
    void Store(RankedDishList list);

    /// <summary>Null when the list never existed or expired.</summary>
    RankedDishList? Get(Guid key);
}
