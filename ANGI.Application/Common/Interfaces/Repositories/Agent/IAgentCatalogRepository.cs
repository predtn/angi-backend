using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Common.Interfaces.Repositories.Agent;

/// <summary>Reads bounded public catalog matches, including temporarily unavailable entries.</summary>
public interface IAgentCatalogRepository
{
    /// <summary>Returns literal name matches from public catalog data, with a caller-owned bound.</summary>
    Task<AgentCatalogMatch[]> SearchAsync(string kind, string name, string? province, int limit, CancellationToken ct);

    /// <summary>Returns the provinces that hold at least one public restaurant.</summary>
    Task<string[]> GetProvincesAsync(CancellationToken ct);

    /// <summary>Returns the province of the nearest public restaurant within the radius, or null when none is that close.</summary>
    Task<string?> NearestProvinceAsync(decimal latitude, decimal longitude, decimal radiusKm, CancellationToken ct);

    /// <summary>Returns the address and coordinates of every public restaurant that has a province.</summary>
    Task<AgentCatalogPlace[]> GetPublicPlacesAsync(CancellationToken ct);
}
