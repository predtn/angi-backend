using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.UseCases.Validators.Agent;
using ANGI.Domain.Enums;

namespace ANGI.Application.Services.Agent;

/// <summary>Enforces source, catalog, budget and route rules before ready state and again before commit.</summary>
public sealed class AgentProposalValidator(IDishRepository dishes, ITagRepository tags, AgentRouteEstimator routes)
{
    /// <summary>Reprices only from the backend and returns blocking errors separately from uncertainty warnings.</summary>
    public async Task<(AgentSnapshot Snapshot, AgentValidation Validation)> ValidateAsync(AgentSnapshot snapshot,
        AgentSnapshot? source, bool confirm, bool calculateRoutes, CancellationToken ct,
        IReadOnlyList<AgentCandidateGrant>? freshGrants = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var state = snapshot.State;
        var emptyUpdate = snapshot.Items.Length == 0 && snapshot.BaseRoadmapId.HasValue;
        var stateValidation = await new AgentStateValidator().ValidateAsync(state, ct);
        errors.AddRange(stateValidation.Errors.Select(x => x.ErrorMessage));
        if (!emptyUpdate && (string.IsNullOrWhiteSpace(state.Province) || state.BudgetAmount is null && !state.BudgetUnrestricted) ||
            !state.NumDays.HasValue || state.PendingQuestion is not null)
            errors.Add("Chưa chốt đủ tỉnh, số ngày, ngân sách hoặc câu hỏi đang chờ.");
        if (snapshot.Items.Length == 0 && !emptyUpdate || snapshot.Items.Length > 720 || snapshot.Items.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemKey) || x.ItemKey.Length > 100 || x.DishId <= 0 ||
                x.DayNumber < 1 || x.DayNumber > state.NumDays || !AgentStateValidator.Meals.Contains(x.MealSlot) || x.SortOrder < 0) ||
            snapshot.Items.Select(x => x.ItemKey).Distinct().Count() != snapshot.Items.Length ||
            snapshot.Items.GroupBy(x => (x.DayNumber, x.MealSlot, x.SortOrder)).Any(x => x.Count() > 1) ||
            snapshot.Items.GroupBy(x => x.DayNumber).Any(x => x.Count() > 24))
            errors.Add("Cấu trúc lịch không hợp lệ hoặc vượt giới hạn.");
        if (errors.Count > 0) return (snapshot, new(errors.ToArray(), [], 0, []));
        var allTags = await tags.GetTagsAsync(null, ct);
        if (state.TagIds.Concat(state.ExcludeTagIds).Concat(state.MealRequests.SelectMany(x => x.TagIds)).Except(allTags.Select(x => x.Id)).Any()) errors.Add("Bộ lọc chứa tag không tồn tại.");
        var catalog = (await dishes.GetCardDishesAsync(snapshot.Items.Select(x => x.DishId).Distinct().ToArray(), ct)).ToDictionary(x => x.Id);
        var original = (source?.Items ?? []).ToDictionary(x => x.ItemKey);
        var normalized = new List<AgentItem>();
        foreach (var item in snapshot.Items)
        {
            if (!catalog.TryGetValue(item.DishId, out var dish) || dish.Status != DishStatus.Active || !dish.IsAvailable || dish.Restaurant is null ||
                dish.Restaurant.DeletedAt is not null || dish.Restaurant.VerificationStatus != RestaurantVerificationStatus.Verified ||
                dish.Restaurant.ModerationStatus != RestaurantModerationStatus.Visible || dish.Restaurant.OperatingStatus != RestaurantOperatingStatus.Open)
            { errors.Add($"Món {item.DishId} không còn khả dụng."); continue; }
            original.TryGetValue(item.ItemKey, out var kept);
            var retained = confirm ? snapshot.RetainedItems?.SingleOrDefault(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId) : null;
            var unchanged = kept?.DishId == item.DishId || retained is not null;
            var refreshed = !confirm && kept?.DishId == item.DishId && source is not null &&
                (AgentScopedPlanning.RequiresRefresh(source.State, state, item) ||
                    (kept.DayNumber != item.DayNumber || kept.MealSlot != item.MealSlot) &&
                    AgentScopedPlanning.Fingerprint(source.State, kept.DayNumber, kept.MealSlot) != AgentScopedPlanning.Fingerprint(state, item.DayNumber, item.MealSlot));
            var authorization = confirm || freshGrants is null ? snapshot.Grants : freshGrants;
            var grant = authorization.LastOrDefault(g => (!g.DayNumber.HasValue || g.DayNumber == item.DayNumber) &&
                (g.MealSlot is null || g.MealSlot == item.MealSlot) && g.DishIds.Contains(item.DishId) &&
                (!confirm || item.RankingFingerprint is null || g.RequestId == item.RequestId &&
                    AgentScopedPlanning.Fingerprint(g.Constraints, item.DayNumber, item.MealSlot) == item.RankingFingerprint));
            if ((!unchanged || refreshed) && grant is null) errors.Add($"Món {item.DishId} cần nguồn ứng viên mới đúng phạm vi và khẩu vị.");
            if (confirm && item.RankingFingerprint is not null && grant is null &&
                !(retained?.RankingFingerprint == item.RankingFingerprint && retained.RequestId == item.RequestId))
                errors.Add($"Nguồn xếp hạng món {item.DishId} không khớp bản đề xuất đã xem.");
            if (item.SourceItemId.HasValue && (!unchanged || item.SourceItemId != kept?.SourceItemId)) errors.Add("Không được gắn ID item nguồn cho món khác.");
            var tagSet = dish.DishTags.Select(x => x.TagId).ToHashSet();
            var requested = AgentScopedPlanning.Request(state, item.DayNumber, item.MealSlot);
            if (requested.DishName is not null && AgentScopedPlanning.Normalize(requested.DishName) != AgentScopedPlanning.Normalize(dish.Name) ||
                requested.RestaurantName is not null && AgentScopedPlanning.Normalize(requested.RestaurantName) != AgentScopedPlanning.Normalize(dish.Restaurant.Name) ||
                requested.RestaurantId.HasValue && requested.RestaurantId != dish.RestaurantId ||
                requested.TagIds.Any(id => !tagSet.Contains(id)))
                errors.Add($"Món {item.DishId} không đáp ứng món/quán hoặc tag được yêu cầu cho ngày/bữa.");
            if (!confirm && requested.DifferentFromPlan && source?.Items.Any(old => old.DishId == item.DishId) == true &&
                (!unchanged || refreshed)) errors.Add($"Món {item.DishId} đã có trong lịch nguồn; cần phương án khác.");
            if (requested.DifferentFromPlan && snapshot.Items.Any(other => other.DishId == item.DishId &&
                (other.DayNumber != item.DayNumber || other.MealSlot != item.MealSlot)))
                errors.Add($"Món {item.DishId} bị lặp ở bữa khác trong lịch; chưa đáp ứng yêu cầu khác món.");
            if (!string.Equals(dish.Restaurant.ProvinceName?.Trim(), state.Province?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                state.ExcludeTagIds.Any(tagSet.Contains) || state.TagIds.Any(id => !tagSet.Contains(id)) ||
                state.MaxPricePerMeal.HasValue && dish.Price > state.MaxPricePerMeal ||
                state.RadiusKm is { } radius && state.OriginLatitude is { } lat && state.OriginLongitude is { } lng && !new NearbyArea(lat, lng, radius).Contains(dish.Restaurant.Latitude, dish.Restaurant.Longitude))
                errors.Add($"Món {item.DishId} không đáp ứng ràng buộc chuyến đi.");
            if (grant is not null && (!unchanged || refreshed))
            {
                var applied = grant.Constraints;
                if (AgentScopedPlanning.Fingerprint(applied, item.DayNumber, item.MealSlot) !=
                    AgentScopedPlanning.Fingerprint(state, item.DayNumber, item.MealSlot))
                    errors.Add($"Món {item.DishId} được lấy theo khẩu vị khác; cần lấy gợi ý lại.");
                if (applied.MaxPricePerMeal.HasValue && dish.Price > applied.MaxPricePerMeal || applied.ExcludeTagIds.Any(tagSet.Contains) || applied.TagIds.Any(id => !tagSet.Contains(id)) ||
                    applied.RadiusKm is { } r && applied.OriginLatitude is { } a && applied.OriginLongitude is { } b && !new NearbyArea(a, b, r).Contains(dish.Restaurant.Latitude, dish.Restaurant.Longitude))
                    errors.Add($"Món {item.DishId} không còn đáp ứng điều kiện lúc lấy ứng viên.");
            }
            var mealTags = dish.DishTags.Where(x => x.Tag.TagType == TagType.MealTime).Select(x => x.Tag.Code).ToArray();
            if (mealTags.Length > 0 && !mealTags.Contains(item.MealSlot)) errors.Add($"Món {item.DishId} không phù hợp bữa đã chọn.");
            if (confirm && item.Price != dish.Price) errors.Add($"Giá món {item.DishId} đã thay đổi; cần xem đề xuất mới.");
            normalized.Add(item with { Price = dish.Price, DishName = dish.Name, RestaurantName = dish.Restaurant.Name,
                Latitude = dish.Restaurant.Latitude, Longitude = dish.Restaurant.Longitude,
                RequestId = confirm ? item.RequestId ?? grant?.RequestId : unchanged && !refreshed ? kept?.RequestId : grant?.RequestId,
                RankingFingerprint = confirm ? item.RankingFingerprint : unchanged && !refreshed ? kept?.RankingFingerprint :
                    grant is null ? null : AgentScopedPlanning.Fingerprint(grant.Constraints, item.DayNumber, item.MealSlot) });
        }
        if (!emptyUpdate && state.MealSlots.Length > 0)
            for (short day = 1; day <= state.NumDays; day++)
                foreach (var meal in state.MealSlots)
                    if (!normalized.Any(x => x.DayNumber == day && x.MealSlot == meal)) errors.Add($"Chưa có món cho bữa {meal} ngày {day}.");
        var total = normalized.Sum(x => x.Price);
        if (state.BudgetAmount.HasValue && total > state.BudgetAmount) errors.Add("Tổng tiền vượt ngân sách toàn chuyến cho một người.");
        var routeResults = new List<AgentDayRoute>();
        if (errors.Count == 0 && calculateRoutes)
        {
            foreach (var day in normalized.GroupBy(x => x.DayNumber).OrderBy(x => x.Key))
            {
                var ordered = day.OrderBy(x => Array.IndexOf(AgentStateValidator.Meals, x.MealSlot)).ThenBy(x => x.SortOrder).ToArray();
                var result = await routes.EstimateAsync(state, day.Key, ordered, RouteProfile.Driving, ct);
                routeResults.Add(result);
                if (result.IsEstimated) warnings.Add($"Tuyến ngày {day.Key} là ước lượng.");
                if (result.OriginSkipped) warnings.Add($"Bỏ điểm xuất phát ngày {day.Key} vì cách món đầu quá 30 km.");
            }
        }
        if (snapshot.Grants.Any(x => !x.RequestId.HasValue)) warnings.Add("Một phần đề xuất dùng xếp hạng đánh giá thay cho cá nhân hóa.");
        var changes = snapshot.Changes?.Select(change => change.Item is null ? change : change with
            { Item = normalized.FirstOrDefault(item => item.ItemKey == change.Item.ItemKey) ?? change.Item }).ToArray();
        return (snapshot with { Items = AgentJson.OrderItems(normalized), Changes = changes }, new(errors.Distinct().ToArray(), warnings.Distinct().ToArray(), total, routeResults.ToArray()));
    }
}
