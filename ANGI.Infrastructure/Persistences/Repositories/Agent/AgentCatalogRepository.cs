using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Agent;

/// <summary>Queries public names without exposing hidden, unverified or deleted restaurants.</summary>
public sealed class AgentCatalogRepository(ANGIContext context) : IAgentCatalogRepository
{
    /// <inheritdoc />
    public async Task<AgentCatalogMatch[]> SearchAsync(string kind, string name, string? province, int limit, CancellationToken ct)
    {
        var term = name.Trim();
        var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var restaurants = context.Restaurants.AsNoTracking().Where(x =>
            x.VerificationStatus == RestaurantVerificationStatus.Verified && x.ModerationStatus == RestaurantModerationStatus.Visible);
        if (province is not null) restaurants = restaurants.Where(x => x.ProvinceName != null && x.ProvinceName.ToUpper() == province.Trim().ToUpper());
        if (kind == "restaurant")
            return await restaurants.Where(x => EF.Functions.ILike(x.Name, pattern, "\\"))
                .OrderByDescending(x => x.Name.ToUpper() == term.ToUpper()).ThenBy(x => x.Id).Take(limit)
                .Select(x => new AgentCatalogMatch(x.Id, x.Name, x.Id, x.Name, x.ProvinceName, x.AddressLine,
                    x.OperatingStatus == RestaurantOperatingStatus.Open)).ToArrayAsync(ct);
        return await context.Dishes.AsNoTracking().Where(x => restaurants.Select(r => r.Id).Contains(x.RestaurantId) &&
                x.Status != DishStatus.Hidden && x.Status != DishStatus.Removed && EF.Functions.ILike(x.Name, pattern, "\\"))
            .OrderByDescending(x => x.Name.ToUpper() == term.ToUpper()).ThenBy(x => x.Id).Take(limit)
            .Select(x => new AgentCatalogMatch(x.Id, x.Name, x.RestaurantId, x.Restaurant.Name, x.Restaurant.ProvinceName,
                x.Restaurant.AddressLine, x.Status == DishStatus.Active && x.IsAvailable && x.Restaurant.OperatingStatus == RestaurantOperatingStatus.Open))
            .ToArrayAsync(ct);
    }
}
