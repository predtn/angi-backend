using ANGI.Application.Common.Models;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Agent;

/// <summary>Queries public names without exposing hidden, unverified or deleted restaurants.</summary>
public sealed class AgentCatalogRepository(ANGIContext context) : IAgentCatalogRepository
{
    /// <inheritdoc />
    public async Task<AgentCatalogMatch[]> SearchAsync(string kind, string name, string? province, int limit, CancellationToken ct)
    {
        // STEP-01: Prepare term through name.Trim, pattern from LikePattern.Contains and restaurants through context.Restaurants.AsNoTracking().Where.
        var term = name.Trim();
        var pattern = LikePattern.Contains(term);
        var restaurants = context.Restaurants.AsNoTracking().Where(x =>
            x.VerificationStatus == RestaurantVerificationStatus.Verified && x.ModerationStatus == RestaurantModerationStatus.Visible);
        // STEP-02: When province exists, set restaurants.
        // Same match as ranking and suggestions: "Thành phố Hà Nội" and "Hà Nội" are one province. An exact comparison sent every dish of a
        // suggestion made with the full spelling to "outside_province", so it could never be added to the plan (conversation 530).
        if (province is not null)
        {
            var provincePattern = ProvinceSearch.Pattern(province);
            restaurants = restaurants.Where(x => x.ProvinceName != null && EF.Functions.ILike(x.ProvinceName, provincePattern, LikePattern.Escape));
        }
        // STEP-03: Return the result of Take().Select(filtered).ToArrayAsync early when kind is "restaurant".
        if (kind == "restaurant")
            return await restaurants.Where(x => EF.Functions.ILike(x.Name, pattern, "\\"))
                .OrderByDescending(x => x.Name.ToUpper() == term.ToUpper()).ThenBy(x => x.Id).Take(limit)
                .Select(x => new AgentCatalogMatch(x.Id, x.Name, x.Id, x.Name, x.ProvinceName, x.AddressLine,
                    x.OperatingStatus == RestaurantOperatingStatus.Open)).ToArrayAsync(ct);
        // STEP-04: Return the result of Take().Select(filtered).ToArrayAsync.
        return await context.Dishes.AsNoTracking().Where(x => restaurants.Select(r => r.Id).Contains(x.RestaurantId) &&
                x.Status != DishStatus.Hidden && x.Status != DishStatus.Removed && EF.Functions.ILike(x.Name, pattern, "\\"))
            .OrderByDescending(x => x.Name.ToUpper() == term.ToUpper()).ThenBy(x => x.Id).Take(limit)
            .Select(x => new AgentCatalogMatch(x.Id, x.Name, x.RestaurantId, x.Restaurant.Name, x.Restaurant.ProvinceName,
                x.Restaurant.AddressLine, x.Status == DishStatus.Active && x.IsAvailable && x.Restaurant.OperatingStatus == RestaurantOperatingStatus.Open))
            .ToArrayAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string[]> GetProvincesAsync(CancellationToken ct) =>
        (await Public().Where(x => x.ProvinceName != null).Select(x => x.ProvinceName!).Distinct().ToArrayAsync(ct))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Order(StringComparer.Ordinal).ToArray();

    /// <inheritdoc />
    public async Task<string?> NearestProvinceAsync(decimal latitude, decimal longitude, decimal radiusKm, CancellationToken ct)
    {
        // STEP-01: Prepare KmPerDegree, latitudeSpan and longitudeSpan, then call Where(filtered).Select(filtered).ToArrayAsync and keep nearby.
        // Lọc thô theo khung vĩ độ/kinh độ trong SQL rồi tính khoảng cách đúng trong bộ nhớ; không gọi dịch vụ bản đồ nên không tốn phí.
        const double KmPerDegree = 111.32;
        var latitudeSpan = (decimal)((double)radiusKm / KmPerDegree);
        var longitudeSpan = (decimal)((double)radiusKm / (KmPerDegree * Math.Max(0.01, Math.Cos((double)latitude * Math.PI / 180))));
        var nearby = await Public().Where(x => x.ProvinceName != null &&
                x.Latitude >= latitude - latitudeSpan && x.Latitude <= latitude + latitudeSpan &&
                x.Longitude >= longitude - longitudeSpan && x.Longitude <= longitude + longitudeSpan)
            .Select(x => new { x.ProvinceName, x.Latitude, x.Longitude }).ToArrayAsync(ct);
        // STEP-02: Return the result of OrderBy(filtered).Select(filtered).FirstOrDefault.
        return nearby.Select(x => (x.ProvinceName, Km: DistanceKm((double)latitude, (double)longitude, (double)x.Latitude, (double)x.Longitude)))
            .Where(x => x.Km <= (double)radiusKm).OrderBy(x => x.Km).Select(x => x.ProvinceName).FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<AgentCatalogPlace[]> GetPublicPlacesAsync(CancellationToken ct) =>
        await Public().Where(x => x.ProvinceName != null)
            .Select(x => new AgentCatalogPlace(x.ProvinceName!, x.AddressLine, x.Latitude, x.Longitude)).ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<string[]> GetDishNamesAsync(CancellationToken ct) =>
        await context.Dishes.AsNoTracking().Where(x => Public().Select(r => r.Id).Contains(x.RestaurantId) && x.Status != DishStatus.Hidden && x.Status != DishStatus.Removed)
            .Select(x => x.Name).ToArrayAsync(ct);

    /// <summary>Restricts every catalog read to verified restaurants that moderation has not hidden.</summary>
    private IQueryable<Domain.Entities.Restaurant> Public() => context.Restaurants.AsNoTracking().Where(x =>
        x.VerificationStatus == RestaurantVerificationStatus.Verified && x.ModerationStatus == RestaurantModerationStatus.Visible);

    /// <summary>Great-circle distance between two coordinates.</summary>
    private static double DistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        // STEP-01: Prepare EarthRadiusKm, dLat, dLng and a from Math.Sin.
        const double EarthRadiusKm = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(dLng / 2), 2);
        // STEP-02: Return the computed condition.
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
