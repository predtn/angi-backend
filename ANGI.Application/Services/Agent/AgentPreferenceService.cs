using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Reads profile data outside database locks and preserves pending user targets on failure.</summary>
public sealed class AgentPreferenceService(IRecommendationService recommendation, TimeProvider? clock = null)
{
    public async Task<AgentState> EnsureAsync(AgentState state, int userId, CancellationToken ct)
    {
        var preferences = state.Preferences;
        if (preferences?.Initialized != true)
        {
            var snapshot = await recommendation.GetPreferencesAsync(userId, ct);
            if (snapshot is null || snapshot.UserId != userId || snapshot.OntologyVersion != 1 || !AgentTasteOntology.ValidVector(snapshot.Vec))
                return state with { Preferences = preferences ?? new() };
            preferences = new() { Initialized = true, BaselineVector = snapshot.Vec?.ToArray(), ProfileUpdatedAt = snapshot.UpdatedAt,
                ProfileReadAt = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime };
        }
        return state with { Preferences = AgentTasteOntology.Apply(preferences, state.TasteTargets) };
    }
}
