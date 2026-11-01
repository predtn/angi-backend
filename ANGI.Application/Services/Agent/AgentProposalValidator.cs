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
            !state.NumDays.HasValue || state.PendingQuestion is not null || state.PendingPlanningRequest is not null)
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
        // Món chỉ thiếu grant đúng ngày/bữa; nếu đó là lỗi duy nhất, orchestrator có thể tự thay bằng món đã được cấp.
        var scopeRejected = new List<string>();
        // changed: món mới hoặc được chọn lại trong lượt này; added: vị trí đổi sang món khác món nguồn.
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            if (!catalog.TryGetValue(item.DishId, out var dish) || !IsPublic(dish))
            { errors.Add($"Món {item.DishId} không còn khả dụng."); continue; }
            original.TryGetValue(item.ItemKey, out var kept);
            var retained = confirm ? snapshot.RetainedItems?.SingleOrDefault(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId) : null;
            var unchanged = kept?.DishId == item.DishId || retained is not null;
            var refreshed = !confirm && kept?.DishId == item.DishId && source is not null &&
                (snapshot.RetainedItems is not null && !snapshot.RetainedItems.Any(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId) ||
                    AgentScopedPlanning.RequiresRefresh(source.State, state, item) ||
                    (kept.DayNumber != item.DayNumber || kept.MealSlot != item.MealSlot) &&
                    AgentScopedPlanning.Fingerprint(source.State, kept.DayNumber, kept.MealSlot) != AgentScopedPlanning.Fingerprint(state, item.DayNumber, item.MealSlot));
            if (!unchanged || refreshed) changed.Add(item.ItemKey);
            if (!unchanged) added.Add(item.ItemKey);
            var authorization = confirm || freshGrants is null ? snapshot.Grants : freshGrants;
            var grant = authorization.LastOrDefault(g => (!g.DayNumber.HasValue || g.DayNumber == item.DayNumber) &&
                (g.MealSlot is null || g.MealSlot == item.MealSlot) && g.DishIds.Contains(item.DishId) &&
                (!confirm || item.RankingFingerprint is null || g.RequestId == item.RequestId &&
                    AgentScopedPlanning.Fingerprint(g.Constraints, item.DayNumber, item.MealSlot) == item.RankingFingerprint));
            if ((!unchanged || refreshed) && grant is null)
            {
                var issuedScopes = authorization.Where(g => g.DishIds.Contains(item.DishId))
                    .Select(g => $"ngày {g.DayNumber?.ToString() ?? "bất kỳ"}, bữa {g.MealSlot ?? "bất kỳ"}").Distinct().ToArray();
                errors.Add($"Món {item.DishId} cần nguồn ứng viên mới đúng phạm vi và khẩu vị. " +
                    $"Vị trí bị từ chối: ngày {item.DayNumber}, bữa {item.MealSlot}. " +
                    (issuedScopes.Length == 0 ? "Món chưa có trong kết quả được cấp; chọn món khác từ kết quả hiện có."
                        : $"Phạm vi đã cấp: {string.Join("; ", issuedScopes)}. Chọn món đã được cấp cho đúng ngày/bữa; không dùng tag bữa ăn để mở rộng phạm vi."));
                scopeRejected.Add(item.ItemKey);
            }
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
            if (!FitsMeal(dish, item.MealSlot)) errors.Add($"Món {item.DishId} không phù hợp bữa đã chọn.");
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
        // Bữa sáng/trưa/tối mặc định cần một món chính. Chỉ kiểm tra bữa có món mới/được chọn lại trong lượt này
        // để lịch đã lưu trước quy tắc này vẫn sửa tiếp được; xác nhận lưu không áp lại quy tắc cho bản đã xem.
        var mainTag = allTags.FirstOrDefault(tag => tag.TagType == TagType.Category && tag.Code == AgentScopedPlanning.MainTagCode);
        if (!confirm && mainTag is not null)
            foreach (var (day, meal) in normalized.Where(x => changed.Contains(x.ItemKey)).Select(x => (x.DayNumber, x.MealSlot)).Distinct())
                if (AgentScopedPlanning.RequiresMain(state, day, meal, allTags) &&
                    !normalized.Any(x => x.DayNumber == day && x.MealSlot == meal && catalog[x.DishId].DishTags.Any(tag => tag.TagId == mainTag.Id)))
                    errors.Add($"Bữa {meal} ngày {day} cần ít nhất một món chính; đồ uống, tráng miệng và món ăn vặt chỉ là món phụ trừ khi người dùng yêu cầu riêng cho bữa đó.");
        var total = normalized.Sum(x => x.Price);
        if (state.BudgetAmount.HasValue && total > state.BudgetAmount) errors.Add("Tổng tiền vượt ngân sách toàn chuyến cho một người.");
        var repeats = !confirm && errors.Count == 0 && freshGrants is { Count: > 0 }
            ? await AvoidableRepeatsAsync(normalized, added, state, freshGrants, catalog, allTags, mainTag, ct) : [];
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
        if (normalized.Any(x => !x.RequestId.HasValue)) warnings.Add("Một phần đề xuất dùng xếp hạng đánh giá thay cho cá nhân hóa.");
        var changes = snapshot.Changes?.Select(change => change.Item is null ? change : change with
            { Item = normalized.FirstOrDefault(item => item.ItemKey == change.Item.ItemKey) ?? change.Item }).ToArray();
        var onlyScopeErrors = scopeRejected.Count > 0 && errors.Count == scopeRejected.Count;
        return (snapshot with { Items = AgentJson.OrderItems(normalized), Changes = changes }, new(errors.Distinct().ToArray(), warnings.Distinct().ToArray(), total,
            routeResults.ToArray(), onlyScopeErrors ? scopeRejected.ToArray() : null, repeats.Length > 0 ? repeats : null));
    }

    /// <summary>
    /// Finds new items whose dish name already appears earlier in the itinerary although an unused dish authorized for the
    /// same position still satisfies its meal, requested tags, main-dish role, price cap and budget. Requested names are exempt.
    /// </summary>
    private async Task<string[]> AvoidableRepeatsAsync(IReadOnlyList<AgentItem> items, IReadOnlySet<string> added, AgentState state,
        IReadOnlyList<AgentCandidateGrant> grants, IReadOnlyDictionary<int, ANGI.Domain.Entities.Dish> catalog,
        IReadOnlyCollection<ANGI.Domain.Entities.Tag> allTags, ANGI.Domain.Entities.Tag? mainTag, CancellationToken ct)
    {
        var missing = grants.SelectMany(grant => grant.DishIds).Where(id => !catalog.ContainsKey(id)).Distinct().ToArray();
        var known = missing.Length == 0 ? catalog
            : catalog.Values.Concat(await dishes.GetCardDishesAsync(missing, ct)).DistinctBy(dish => dish.Id).ToDictionary(dish => dish.Id);
        var used = items.Select(item => AgentScopedPlanning.Normalize(catalog[item.DishId].Name)!).ToHashSet(StringComparer.Ordinal);
        var total = items.Sum(item => item.Price);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var repeats = new List<string>();
        foreach (var item in AgentJson.OrderItems(items))
        {
            if (seen.Add(AgentScopedPlanning.Normalize(catalog[item.DishId].Name)!) || !added.Contains(item.ItemKey)) continue;
            var requested = AgentScopedPlanning.Request(state, item.DayNumber, item.MealSlot);
            if (requested.DishName is not null || requested.RestaurantName is not null) continue;
            var fingerprint = AgentScopedPlanning.Fingerprint(state, item.DayNumber, item.MealSlot);
            var main = mainTag is not null && AgentScopedPlanning.RequiresMain(state, item.DayNumber, item.MealSlot, allTags) &&
                catalog[item.DishId].DishTags.Any(tag => tag.TagId == mainTag.Id);
            var spare = grants.Where(grant => (!grant.DayNumber.HasValue || grant.DayNumber == item.DayNumber) &&
                    (grant.MealSlot is null || grant.MealSlot == item.MealSlot) &&
                    AgentScopedPlanning.Fingerprint(grant.Constraints, item.DayNumber, item.MealSlot) == fingerprint)
                .SelectMany(grant => grant.DishIds).Any(id => known.TryGetValue(id, out var dish) && IsPublic(dish) &&
                    FitsMeal(dish, item.MealSlot) && !used.Contains(AgentScopedPlanning.Normalize(dish.Name)!) &&
                    requested.TagIds.All(tag => dish.DishTags.Any(x => x.TagId == tag)) &&
                    (!main || dish.DishTags.Any(x => x.TagId == mainTag!.Id)) &&
                    (!state.MaxPricePerMeal.HasValue || dish.Price <= state.MaxPricePerMeal) &&
                    (state.BudgetAmount is not { } budget || total - item.Price + dish.Price <= budget));
            if (spare) repeats.Add(item.ItemKey);
        }
        return repeats.ToArray();
    }

    /// <summary>
    /// Replaces only items rejected for a missing day/meal grant, or avoidable repeats, with the best-ranked unused dish
    /// already issued for that exact position under the same effective constraints. Returns null when a safe replacement does not exist.
    /// </summary>
    public async Task<AgentSnapshot?> RepairScopeAsync(AgentSnapshot snapshot, AgentSnapshot? source,
        IReadOnlyList<AgentCandidateGrant> grants, IReadOnlyCollection<string> itemKeys, CancellationToken ct, bool repeatsOnly = false)
    {
        var state = snapshot.State;
        var scope = snapshot.Scope ?? new();
        var targets = snapshot.Items.Where(item => itemKeys.Contains(item.ItemKey)).ToArray();
        var sourceItems = source?.Items ?? [];
        // Chỉ sửa món mới trong phạm vi model được phép đổi. Nhiều vị trí sai cùng lúc cho thấy model hiểu sai yêu cầu,
        // khi đó để model tự sửa thay vì backend thay phần lớn lịch. Món lặp đã đúng vị trí nên không cần giới hạn này.
        if (targets.Length == 0 || targets.Length != itemKeys.Count || !repeatsOnly && targets.Length > Math.Max(1, snapshot.Items.Length / 3) ||
            targets.Any(item => !scope.Contains(item) || sourceItems.Any(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId)))
            return null;
        // Grant hẹp và mới hơn đứng trước; trong mỗi grant giữ nguyên thứ tự xếp hạng của Recommendation.
        // Chỉ dùng grant có cùng fingerprint (khẩu vị, món/quán/tag yêu cầu) với vị trí cần thay.
        var options = targets.ToDictionary(item => item.ItemKey, item => grants
            .Select((grant, order) => (grant, order))
            .Where(x => (!x.grant.DayNumber.HasValue || x.grant.DayNumber == item.DayNumber) &&
                (x.grant.MealSlot is null || x.grant.MealSlot == item.MealSlot) &&
                AgentScopedPlanning.Fingerprint(x.grant.Constraints, item.DayNumber, item.MealSlot) ==
                AgentScopedPlanning.Fingerprint(state, item.DayNumber, item.MealSlot))
            .OrderByDescending(x => (x.grant.DayNumber.HasValue ? 2 : 0) + (x.grant.MealSlot is null ? 0 : 1)).ThenByDescending(x => x.order)
            .SelectMany(x => x.grant.DishIds).Distinct().ToArray());
        var catalog = (await dishes.GetCardDishesAsync(options.Values.SelectMany(ids => ids)
            .Concat(snapshot.Items.Select(item => item.DishId)).Distinct().ToArray(), ct)).ToDictionary(x => x.Id);
        var kept = snapshot.Items.Where(item => !itemKeys.Contains(item.ItemKey)).ToArray();
        // Không chọn lại món đang có trong lịch hoặc món cũ của nguồn để thay đổi vẫn là một lựa chọn mới.
        var usedIds = kept.Select(item => item.DishId).Concat(sourceItems.Select(item => item.DishId)).ToHashSet();
        var usedNames = kept.Where(item => catalog.ContainsKey(item.DishId))
            .Select(item => AgentScopedPlanning.Normalize(catalog[item.DishId].Name)!).ToHashSet(StringComparer.Ordinal);
        decimal PriceOf(AgentItem item) => catalog.TryGetValue(item.DishId, out var dish) ? dish.Price : 0;
        // Món lặp có thể được giữ nguyên nên tổng tiền tính cả giá hiện tại của nó; thay món thì trừ giá cũ ra.
        var total = kept.Sum(PriceOf) + (repeatsOnly ? targets.Sum(PriceOf) : 0);
        var replacements = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in targets)
        {
            var current = repeatsOnly ? PriceOf(item) : 0;
            var fits = options[item.ItemKey].Where(id => catalog.TryGetValue(id, out var dish) && IsPublic(dish) && FitsMeal(dish, item.MealSlot) &&
                !usedIds.Contains(id) && (!state.MaxPricePerMeal.HasValue || dish.Price <= state.MaxPricePerMeal) &&
                (state.BudgetAmount is not { } budget || total - current + dish.Price <= budget)).ToArray();
            // Ưu tiên món khác tên với các món còn lại để lịch không lặp; nếu không có thì lấy món xếp hạng cao nhất hợp lệ.
            var unusedName = fits.Where(id => !usedNames.Contains(AgentScopedPlanning.Normalize(catalog[id].Name)!)).Select(id => (int?)id).FirstOrDefault();
            // Sửa món lặp chỉ có ích khi còn món khác tên. Vị trí đã hết lựa chọn thì giữ món cũ và sửa tiếp các vị trí khác,
            // không bỏ cả lượt sửa chỉ vì một bữa (ví dụ ngày cuối chỉ còn các món đã dùng).
            if (repeatsOnly && unusedName is null)
            {
                usedIds.Add(item.DishId);
                if (catalog.TryGetValue(item.DishId, out var keptDish)) usedNames.Add(AgentScopedPlanning.Normalize(keptDish.Name)!);
                continue;
            }
            if ((unusedName ?? fits.Select(id => (int?)id).FirstOrDefault()) is not { } replacement) return null;
            replacements[item.ItemKey] = replacement;
            usedIds.Add(replacement);
            usedNames.Add(AgentScopedPlanning.Normalize(catalog[replacement].Name)!);
            total += catalog[replacement].Price - current;
        }
        if (replacements.Count == 0) return null;
        // Giá, tên, provenance được validator tính lại; grant được thu gọn như builder để còn dùng khi xác nhận.
        var items = snapshot.Items.Select(item => replacements.TryGetValue(item.ItemKey, out var id)
            ? new AgentItem(item.ItemKey, null, item.DayNumber, item.MealSlot, item.SortOrder, id) : item).ToArray();
        var authorizations = snapshot.Grants.Concat(grants).Select(grant => grant with
            {
                DishIds = grant.DishIds.Where(id => items.Any(item => item.DishId == id &&
                    (!grant.DayNumber.HasValue || grant.DayNumber == item.DayNumber) && (grant.MealSlot is null || grant.MealSlot == item.MealSlot))).ToArray()
            }).Where(grant => grant.DishIds.Length > 0)
            .DistinctBy(grant => (grant.DayNumber, grant.MealSlot, grant.RequestId, string.Join(',', grant.DishIds))).ToArray();
        return snapshot with { Items = AgentJson.OrderItems(items), Grants = authorizations,
            Changes = snapshot.Changes?.Select(change => change.Item is { } item && replacements.ContainsKey(item.ItemKey)
                ? change with { Item = items.Single(x => x.ItemKey == item.ItemKey) } : change).ToArray() };
    }

    /// <summary>Only active, available dishes of a verified, visible and open restaurant can enter a proposal.</summary>
    private static bool IsPublic(ANGI.Domain.Entities.Dish dish) => dish.Status == DishStatus.Active && dish.IsAvailable && dish.Restaurant is not null &&
        dish.Restaurant.DeletedAt is null && dish.Restaurant.VerificationStatus == RestaurantVerificationStatus.Verified &&
        dish.Restaurant.ModerationStatus == RestaurantModerationStatus.Visible && dish.Restaurant.OperatingStatus == RestaurantOperatingStatus.Open;

    /// <summary>A dish without meal-time tags fits any meal; otherwise its tags must include the selected meal.</summary>
    private static bool FitsMeal(ANGI.Domain.Entities.Dish dish, string meal)
    {
        var mealTags = dish.DishTags.Where(x => x.Tag.TagType == TagType.MealTime).Select(x => x.Tag.Code).ToArray();
        return mealTags.Length == 0 || mealTags.Contains(meal);
    }
}
