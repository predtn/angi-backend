using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.UseCases.Validators.Agent;
using FluentValidation;

namespace ANGI.Application.Services.Agent.Constraints;

/// <summary>Accepts validated state enrichment while preventing the model from relaxing hard constraints.</summary>
public sealed class AgentStatePolicy
{
    // Chỉ khi người dùng tự nhắc tới điểm chấm/bán kính thì model mới được bỏ chúng.
    private static readonly Regex NamesOrigin = new(@"\b(?:ban kinh|diem xuat phat|diem (?:da )?cham|ban do|vi tri|ghim|toan (?:thanh pho|tinh|tp)|ca (?:thanh pho|tinh|tp))\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// A trip value the model left empty is not a request to erase it: conversation 138 turned "chỉ gợi ý một món" into a state with no duration, budget or pin,
    /// and a following "ok" wiped them. Removing a budget limit goes through budgetUnrestricted; a pin goes only when the user names it.
    /// </summary>
    public static AgentState? WithoutErased(AgentState? proposed, AgentState current, string content)
    {
        if (proposed is null) return null;
        var plain = new string(content.Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        var keepsOrigin = !NamesOrigin.IsMatch(plain);
        var origin = keepsOrigin && proposed.OriginMode == "none" && current.OriginMode != "none"
            ? proposed with { OriginMode = current.OriginMode, OriginLatitude = current.OriginLatitude, OriginLongitude = current.OriginLongitude, OriginLabel = current.OriginLabel } : proposed;
        return origin with
        {
            Province = string.IsNullOrWhiteSpace(proposed.Province) ? current.Province : proposed.Province,
            NumDays = proposed.NumDays ?? current.NumDays,
            BudgetAmount = proposed.BudgetAmount ?? (proposed.BudgetUnrestricted ? null : current.BudgetAmount),
            RadiusKm = proposed.RadiusKm ?? (keepsOrigin ? current.RadiusKm : null)
        };
    }

    /// <summary>Requires a backend-staged human confirmation before relaxing an existing trip requirement.</summary>
    public AgentState Accept(AgentState current, AgentState? proposed, AgentState? baseline = null)
    {
        if (proposed is null && current.PendingPlanningRequest is null) return current;
        proposed ??= current;
        if (proposed.PendingPlanningRequest != current.PendingPlanningRequest || proposed.PendingSave != current.PendingSave || proposed.PlanningIntake != current.PlanningIntake)
            proposed = proposed with { PendingPlanningRequest = current.PendingPlanningRequest, PendingSave = current.PendingSave, PlanningIntake = current.PlanningIntake };
        // Taste scopes belong to overrides/requests, not navigation focus. A model cannot invent or clear a UI pin.
        if (proposed.FocusDay != current.FocusDay || proposed.FocusMeal != current.FocusMeal)
            proposed = proposed with { FocusDay = current.FocusDay, FocusMeal = current.FocusMeal };
        if (current.PendingPlanningRequest is { } pendingRequest)
            proposed = AgentPendingPlanningPolicy.Apply(proposed, pendingRequest);
        // Provider and client state can only change targets; trusted vectors always come from reco.
        if (proposed.Preferences != current.Preferences) proposed = proposed with { Preferences = current.Preferences };
        if (!ReferenceEquals(proposed.ReplacedDishNames, current.ReplacedDishNames) || !ReferenceEquals(proposed.SkippedMeals, current.SkippedMeals))
            proposed = proposed with { ReplacedDishNames = current.ReplacedDishNames, SkippedMeals = current.SkippedMeals };
        // Nơi đến theo ngày do backend đọc từ lời người dùng, model không được ghi. Đổi tỉnh hoặc rút ngắn chuyến thì bỏ các nơi đến không còn áp dụng.
        var movedProvince = current.Province is not null && !string.Equals(current.Province.Trim(), proposed.Province?.Trim(), StringComparison.OrdinalIgnoreCase);
        var numDays = proposed.NumDays;
        var areas = movedProvince ? [] : (current.DayAreas ?? []).Where(x => !numDays.HasValue || x.DayNumber <= numDays).ToArray();
        if (!areas.SequenceEqual(proposed.DayAreas ?? [])) proposed = proposed with { DayAreas = areas.Length > 0 ? areas : null };
        var widened = movedProvince ? [] : (current.WidenedMeals ?? []).Where(x => !numDays.HasValue || x.DayNumber <= numDays).ToArray();
        if (!widened.SequenceEqual(proposed.WidenedMeals ?? [])) proposed = proposed with { WidenedMeals = widened.Length > 0 ? widened : null };
        if (proposed.PendingDayArea != current.PendingDayArea || proposed.NamedAreaLabel != current.NamedAreaLabel)
            proposed = proposed with { PendingDayArea = current.PendingDayArea, NamedAreaLabel = current.NamedAreaLabel };
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
