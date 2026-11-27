using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Common.Interfaces.Services.Agent;

/// <summary>
/// Picks, from dishes the backend already filtered by hard conditions, the ones that fit a wish no filter can express ("đặc trưng Hà Nội", "độc lạ", "no").
/// The picker only names ids of the list it was given; the caller keeps the ones that really were in it.
/// </summary>
public interface IAgentDishPicker
{
    /// <summary>The picks in order of fit, or null when the provider failed; the caller then ranks the list without the wish.</summary>
    Task<AgentDishPick?> PickAsync(AgentDishPickRequest request, CancellationToken ct);
}
