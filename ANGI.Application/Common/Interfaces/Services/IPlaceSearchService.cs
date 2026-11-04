using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Common.Interfaces.Services;

/// <summary>Looks up named places on a map; an unavailable service returns no candidates rather than failing the chat turn.</summary>
public interface IPlaceSearchService
{
    /// <summary>Returns the best candidates for the query, nearest to the optional point first when the provider supports it.</summary>
    Task<AgentPlaceCandidate[]> SearchAsync(string query, decimal? nearLatitude, decimal? nearLongitude, CancellationToken ct);
}
