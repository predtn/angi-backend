using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Common.Interfaces.Repositories.Agent;

/// <summary>Reads bounded public catalog matches, including temporarily unavailable entries.</summary>
public interface IAgentCatalogRepository
{
    /// <summary>Returns literal name matches from public catalog data, with a caller-owned bound.</summary>
    Task<AgentCatalogMatch[]> SearchAsync(string kind, string name, string? province, int limit, CancellationToken ct);
}
