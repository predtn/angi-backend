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
        if (state.TagIds.Concat(state.ExcludeTagIds).Except(allTags.Select(x => x.Id)).Any()) errors.Add("Bộ lọc chứa tag không tồn tại.");
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
            var authorization = confirm || freshGrants is null ? snapshot.Grants : freshGrants;
            var grant = authorization.LastOrDefault(g => (!g.DayNumber.HasValue || g.DayNumber == item.DayNumber) &&
                (g.MealSlot is null || g.MealSlot == item.MealSlot) && g.DishIds.Contains(item.DishId));
            if (!unchanged && grant is null) errors.Add($"Món {item.DishId} không có nguồn ứng viên đúng phạm vi.");
            if (item.SourceItemId.HasValue && (!unchanged || item.SourceItemId != kept?.SourceItemId)) errors.Add("Không được gắn ID item nguồn cho món khác.");
            var tagSet = dish.DishTags.Select(x => x.TagId).ToHashSet();
            if (!string.Equals(dish.Restaurant.ProvinceName?.Trim(), state.Province?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                state.ExcludeTagIds.Any(tagSet.Contains) || state.TagIds.Any(id => !tagSet.Contains(id)) ||
                state.MaxPricePerMeal.HasValue && dish.Price > state.MaxPricePerMeal ||
                state.RadiusKm is { } radius && state.OriginLatitude is { } lat && state.OriginLongitude is { } lng && !new NearbyArea(lat, lng, radius).Contains(dish.Restaurant.Latitude, dish.Restaurant.Longitude))
                errors.Add($"Món {item.DishId} không đáp ứng ràng buộc chuyến đi.");
            if (grant is not null && !unchanged)
            {
                var applied = grant.Constraints;
                if (applied.MaxPricePerMeal.HasValue && dish.Price > applied.MaxPricePerMeal || applied.ExcludeTagIds.Any(tagSet.Contains) || applied.TagIds.Any(id => !tagSet.Contains(id)) ||
                    applied.RadiusKm is { } r && applied.OriginLatitude is { } a && applied.OriginLongitude is { } b && !new NearbyArea(a, b, r).Contains(dish.Restaurant.Latitude, dish.Restaurant.Longitude))
                    errors.Add($"Món {item.DishId} không còn đáp ứng điều kiện lúc lấy ứng viên.");
            }
            var mealTags = dish.DishTags.Where(x => x.Tag.TagType == TagType.MealTime).Select(x => x.Tag.Code).ToArray();
            if (mealTags.Length > 0 && !mealTags.Contains(item.MealSlot)) errors.Add($"Món {item.DishId} không phù hợp bữa đã chọn.");
            if (confirm && item.Price != dish.Price) errors.Add($"Giá món {item.DishId} đã thay đổi; cần xem đề xuất mới.");
            normalized.Add(item with { Price = dish.Price, DishName = dish.Name, RestaurantName = dish.Restaurant.Name,
                Latitude = dish.Restaurant.Latitude, Longitude = dish.Restaurant.Longitude,
                RequestId = unchanged ? kept is not null && kept.DishId == item.DishId ? kept.RequestId : retained?.RequestId : grant?.RequestId });
        }
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
        return (snapshot with { Items = normalized.ToArray(), Changes = changes }, new(errors.Distinct().ToArray(), warnings.Distinct().ToArray(), total, routeResults.ToArray()));
    }
}
