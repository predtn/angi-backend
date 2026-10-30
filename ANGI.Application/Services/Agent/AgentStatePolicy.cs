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
        // Provider and client state can only change targets; trusted vectors always come from reco.
        if (proposed.Preferences != current.Preferences) proposed = proposed with { Preferences = current.Preferences };
        new AgentStateValidator().ValidateAndThrow(proposed);
        if (proposed.Preferences?.Initialized == true)
            proposed = proposed with { Preferences = AgentTasteOntology.Apply(proposed.Preferences, proposed.TasteTargets) };
        var confirmed = baseline ?? current;
        var relaxed = confirmed.Province is not null && !string.Equals(confirmed.Province.Trim(), proposed.Province?.Trim(), StringComparison.OrdinalIgnoreCase)
            || confirmed.NumDays.HasValue && proposed.NumDays != confirmed.NumDays
            || confirmed.BudgetAmount.HasValue && (!proposed.BudgetAmount.HasValue || proposed.BudgetAmount > confirmed.BudgetAmount)
            || !confirmed.BudgetUnrestricted && proposed.BudgetUnrestricted
            || confirmed.MaxPricePerMeal.HasValue && (!proposed.MaxPricePerMeal.HasValue || proposed.MaxPricePerMeal > confirmed.MaxPricePerMeal)
            || confirmed.ExcludeTagIds.Except(proposed.ExcludeTagIds).Any()
            || confirmed.TagIds.Except(proposed.TagIds).Any()
            || AgentScopedPlanning.Slots(confirmed, new()).Any(slot =>
            {
                var before = AgentScopedPlanning.Request(confirmed, slot.Day, slot.Meal);
                var after = AgentScopedPlanning.Request(proposed, slot.Day, slot.Meal);
                return before.TagIds.Except(after.TagIds).Any() || before.DifferentFromPlan && !after.DifferentFromPlan ||
                    before.DishName is not null && AgentScopedPlanning.Normalize(before.DishName) != AgentScopedPlanning.Normalize(after.DishName) ||
                    before.RestaurantName is not null && (AgentScopedPlanning.Normalize(before.RestaurantName) != AgentScopedPlanning.Normalize(after.RestaurantName) ||
                        before.RestaurantId.HasValue && before.RestaurantId != after.RestaurantId);
            })
            || confirmed.RadiusKm.HasValue && (!proposed.RadiusKm.HasValue || proposed.RadiusKm > confirmed.RadiusKm)
            || confirmed.OriginMode != "none" && (proposed.OriginMode != confirmed.OriginMode || proposed.OriginLatitude != confirmed.OriginLatitude || proposed.OriginLongitude != confirmed.OriginLongitude);
        if (relaxed) throw new AgentStateChangeRequiredException(proposed);
        if (proposed.PendingStateChange == current.PendingStateChange && proposed.SourceRoadmapUpdatedAt == current.SourceRoadmapUpdatedAt &&
            proposed.HistoryStartSequence == current.HistoryStartSequence) return proposed;
        return proposed with { PendingStateChange = current.PendingStateChange, SourceRoadmapUpdatedAt = current.SourceRoadmapUpdatedAt,
            HistoryStartSequence = current.HistoryStartSequence };
    }
}
