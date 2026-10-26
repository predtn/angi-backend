using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using FluentValidation;

namespace ANGI.Application.Services.Agent;

/// <summary>Accepts validated state enrichment while preventing the model from relaxing hard constraints.</summary>
public sealed class AgentStatePolicy
{
    /// <summary>Requires explicit UI-confirmed state for changes that relax an existing trip requirement.</summary>
    public AgentState Accept(AgentState current, AgentState? proposed)
    {
        if (proposed is null) return current;
        new AgentStateValidator().ValidateAndThrow(proposed);
        var relaxed = current.Province is not null && !string.Equals(current.Province.Trim(), proposed.Province?.Trim(), StringComparison.OrdinalIgnoreCase)
            || current.NumDays.HasValue && proposed.NumDays != current.NumDays
            || current.BudgetAmount.HasValue && (!proposed.BudgetAmount.HasValue || proposed.BudgetAmount > current.BudgetAmount)
            || !current.BudgetUnrestricted && proposed.BudgetUnrestricted
            || current.MaxPricePerMeal.HasValue && (!proposed.MaxPricePerMeal.HasValue || proposed.MaxPricePerMeal > current.MaxPricePerMeal)
            || current.ExcludeTagIds.Except(proposed.ExcludeTagIds).Any()
            || current.TagIds.Except(proposed.TagIds).Any()
            || current.RadiusKm.HasValue && (!proposed.RadiusKm.HasValue || proposed.RadiusKm > current.RadiusKm)
            || current.OriginMode != "none" && (proposed.OriginMode != current.OriginMode || proposed.OriginLatitude != current.OriginLatitude || proposed.OriginLongitude != current.OriginLongitude);
        if (relaxed) throw new ConflictException("PROPOSAL_NOT_COMMITTABLE", "Cần xác nhận rõ thay đổi ràng buộc chuyến đi qua confirmedState.");
        return proposed;
    }
}
