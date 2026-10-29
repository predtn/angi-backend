using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using FluentValidation;

namespace ANGI.Application.Services.Agent;

/// <summary>Accepts validated state enrichment while preventing the model from relaxing hard constraints.</summary>
public sealed class AgentStatePolicy
{
    /// <summary>Requires a backend-staged human confirmation before relaxing an existing trip requirement.</summary>
    public AgentState Accept(AgentState current, AgentState? proposed, AgentState? baseline = null)
    {
        if (proposed is null) return current;
        new AgentStateValidator().ValidateAndThrow(proposed);
        var confirmed = baseline ?? current;
        var relaxed = confirmed.Province is not null && !string.Equals(confirmed.Province.Trim(), proposed.Province?.Trim(), StringComparison.OrdinalIgnoreCase)
            || confirmed.NumDays.HasValue && proposed.NumDays != confirmed.NumDays
            || confirmed.BudgetAmount.HasValue && (!proposed.BudgetAmount.HasValue || proposed.BudgetAmount > confirmed.BudgetAmount)
            || !confirmed.BudgetUnrestricted && proposed.BudgetUnrestricted
            || confirmed.MaxPricePerMeal.HasValue && (!proposed.MaxPricePerMeal.HasValue || proposed.MaxPricePerMeal > confirmed.MaxPricePerMeal)
            || confirmed.ExcludeTagIds.Except(proposed.ExcludeTagIds).Any()
            || confirmed.TagIds.Except(proposed.TagIds).Any()
            || confirmed.RadiusKm.HasValue && (!proposed.RadiusKm.HasValue || proposed.RadiusKm > confirmed.RadiusKm)
            || confirmed.OriginMode != "none" && (proposed.OriginMode != confirmed.OriginMode || proposed.OriginLatitude != confirmed.OriginLatitude || proposed.OriginLongitude != confirmed.OriginLongitude);
        if (relaxed) throw new AgentStateChangeRequiredException(proposed);
        if (proposed.PendingStateChange == current.PendingStateChange && proposed.SourceRoadmapVersion == current.SourceRoadmapVersion &&
            proposed.HistoryStartSequence == current.HistoryStartSequence) return proposed;
        return proposed with { PendingStateChange = current.PendingStateChange, SourceRoadmapVersion = current.SourceRoadmapVersion,
            HistoryStartSequence = current.HistoryStartSequence };
    }
}
