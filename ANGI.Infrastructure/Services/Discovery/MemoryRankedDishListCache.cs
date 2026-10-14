using ANGI.Application.Common.Interfaces.Services.Discovery;
using ANGI.Application.Common.Models.Discovery;
using Microsoft.Extensions.Caching.Memory;

namespace ANGI.Infrastructure.Services.Discovery;

/// <summary>
/// Keeps ranked DISH-01 lists in process memory for 30 minutes. Like session coordination, it assumes one backend
/// instance; several instances would need a shared cache.
/// </summary>
public sealed class MemoryRankedDishListCache : IRankedDishListCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _cache;

    public MemoryRankedDishListCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    public void Store(RankedDishList list) =>
        _cache.Set(CacheKey(list.Key), list, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime });

    public RankedDishList? Get(Guid key) =>
        _cache.TryGetValue(CacheKey(key), out RankedDishList? list) ? list : null;

    private static string CacheKey(Guid key) => $"dish-list:{key:N}";
}
