using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.UseCases.Validators.Agent;
using ANGI.Domain.Enums;

namespace ANGI.Application.Services.Agent.Proposals;

/// <summary>Enforces source, catalog, budget and route rules before ready state and again before commit.</summary>
public sealed class AgentProposalValidator(IDishRepository dishes, ITagRepository tags, AgentRouteEstimator routes)
{
    /// <summary>Error of a proposal whose total exceeds the trip budget; the orchestrator turns it into a budget question.</summary>
    public const string BudgetExceeded = "Tổng tiền vượt ngân sách toàn chuyến cho một người.";

    // The parts of this validator's own messages that mean a dish was made up rather than chosen badly: not in the catalog, not issued by a tool for that
    // day, meal and condition, or outside the trip's province or area. Budget, repeats and meal fit are soft and stay warnings (user decision 2026-11-12).
    private static readonly string[] FabricationMarkers = ["không còn khả dụng.", "cần nguồn ứng viên mới đúng phạm vi", "không đáp ứng ràng buộc chuyến đi.",
        "được lấy theo khẩu vị khác", "không còn đáp ứng điều kiện lúc lấy ứng viên"];

    /// <summary>The dish an error is about when it says the dish was made up (see <see cref="FabricationMarkers"/>); null for any other error.</summary>
    public static int? FabricatedDish(string error) =>
        FabricationMarkers.Any(marker => error.Contains(marker, StringComparison.Ordinal)) &&
        System.Text.RegularExpressions.Regex.Match(error, @"^Món (\d+) ") is { Success: true } match ? int.Parse(match.Groups[1].Value) : null;

    /// <summary>
    /// A declared meal the source already lacks (removed earlier, or deleted from the roadmap by hand) is not this edit's gap,
    /// so an unrelated edit is not rejected for it. A day or meal slot new to this plan still has to be filled.
    /// </summary>
    private static bool AlreadyEmpty(AgentSnapshot? source, short day, string meal) => source is not null && day <= source.State.NumDays &&
        source.State.MealSlots.Contains(meal) && !source.Items.Any(item => item.DayNumber == day && item.MealSlot == meal);

    /// <summary>
    /// Fills each item's name, price, restaurant and coordinates from the catalog without judging it, for a proposal shown despite
    /// validation errors; an id the catalog does not know keeps what the model wrote. The total is the sum of the known prices.
    /// </summary>
    public async Task<(AgentSnapshot Snapshot, decimal Total)> ReadCatalogAsync(AgentSnapshot snapshot, CancellationToken ct)
    {
        var catalog = (await dishes.GetCardDishesAsync(snapshot.Items.Select(x => x.DishId).Distinct().ToArray(), ct)).ToDictionary(x => x.Id);
        var items = snapshot.Items.Select(item => catalog.TryGetValue(item.DishId, out var dish)
            ? item with { Price = dish.Price, DishName = dish.Name, RestaurantName = dish.Restaurant.Name,
                Latitude = dish.Restaurant.Latitude, Longitude = dish.Restaurant.Longitude }
            : item).ToArray();
        return (snapshot with { Items = items }, items.Sum(item => item.Price));
    }

    /// <summary>
    /// The errors that still block writing a roadmap: an ill-formed trip or plan, a dish that no longer exists or is not public, a price changed since the
    /// user saw it, and a source item ID moved onto another dish. Plan rules such as budget, meal fit, repeats or candidate scope do not block, because the
    /// chat shows a plan despite them and the user decided to save it.
    /// </summary>
    public async Task<string[]> IntegrityErrorsAsync(AgentSnapshot snapshot, AgentSnapshot? source, CancellationToken ct)
    {
        // STEP-01: Return the structure errors early, since nothing else can be read from an ill-formed plan.
        var errors = await StructureErrorsAsync(snapshot, ct);
        if (errors.Count > 0) return errors.ToArray();
        // STEP-02: Load the catalog dishes and the source items the plan keeps.
        var catalog = (await dishes.GetCardDishesAsync(snapshot.Items.Select(x => x.DishId).Distinct().ToArray(), ct)).ToDictionary(x => x.Id);
        var original = (source?.Items ?? []).ToDictionary(x => x.ItemKey);
        // STEP-03: For each item, report a missing or hidden dish, a changed price and a source item ID that does not belong to it.
        foreach (var item in snapshot.Items)
        {
            if (!catalog.TryGetValue(item.DishId, out var dish) || !IsPublic(dish)) { errors.Add($"Món {item.DishId} không còn khả dụng."); continue; }
            if (item.Price != dish.Price) errors.Add($"Giá món {item.DishId} đã thay đổi; cần xem đề xuất mới.");
            original.TryGetValue(item.ItemKey, out var kept);
            var retained = snapshot.RetainedItems?.SingleOrDefault(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId);
            var unchanged = kept?.DishId == item.DishId || retained is not null;
            if (item.SourceItemId.HasValue && (!unchanged || item.SourceItemId != kept?.SourceItemId)) errors.Add("Không được gắn ID item nguồn cho món khác.");
        }
        // STEP-04: Return the distinct errors.
        return errors.Distinct().ToArray();
    }

    /// <summary>Checks the trip state and the plan's shape: province, days, open questions, keys, day and meal positions and size limits.</summary>
    private static async Task<List<string>> StructureErrorsAsync(AgentSnapshot snapshot, CancellationToken ct)
    {
        // STEP-01: Prepare errors from the state validator.
        var state = snapshot.State;
        var emptyUpdate = snapshot.Items.Length == 0 && snapshot.BaseRoadmapId.HasValue;
        var errors = (await new AgentStateValidator().ValidateAsync(state, ct)).Errors.Select(x => x.ErrorMessage).ToList();
        // STEP-02: Report a missing province or day count and an open question or planning request.
        // Ngân sách không bắt buộc: chưa có ngân sách nghĩa là lịch không giới hạn ngân sách.
        if (!emptyUpdate && string.IsNullOrWhiteSpace(state.Province) ||
            !state.NumDays.HasValue || state.PendingQuestion is not null || state.PendingPlanningRequest is not null)
            errors.Add("Chưa chốt đủ tỉnh, số ngày hoặc câu hỏi đang chờ.");
        // STEP-03: Report an empty, oversized or malformed item list, and return errors.
        if (snapshot.Items.Length == 0 && !emptyUpdate || snapshot.Items.Length > AgentStateValidator.MaxItems || snapshot.Items.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemKey) || x.ItemKey.Length > 100 || x.DishId <= 0 ||
                x.DayNumber < 1 || x.DayNumber > state.NumDays || !AgentStateValidator.Meals.Contains(x.MealSlot) || x.SortOrder < 0) ||
            snapshot.Items.Select(x => x.ItemKey).Distinct().Count() != snapshot.Items.Length ||
            snapshot.Items.GroupBy(x => (x.DayNumber, x.MealSlot, x.SortOrder)).Any(x => x.Count() > 1) ||
            snapshot.Items.GroupBy(x => x.DayNumber).Any(x => x.Count() > 24))
            errors.Add("Cấu trúc lịch không hợp lệ hoặc vượt giới hạn.");
        return errors;
    }

    /// <summary>Reprices only from the backend and returns blocking errors separately from uncertainty warnings.</summary>
    public async Task<(AgentSnapshot Snapshot, AgentValidation Validation)> ValidateAsync(AgentSnapshot snapshot,
        AgentSnapshot? source, bool confirm, bool calculateRoutes, CancellationToken ct,
        IReadOnlyList<AgentCandidateGrant>? freshGrants = null, IReadOnlyList<AgentCandidateGrant>? spareGrants = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var state = snapshot.State;
        var emptyUpdate = snapshot.Items.Length == 0 && snapshot.BaseRoadmapId.HasValue;
        errors.AddRange(await StructureErrorsAsync(snapshot, ct));
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
            if (requested.DishName is not null && !AgentScopedPlanning.FitsName(requested.DishName, dish.Name) ||
                requested.RestaurantName is not null && AgentScopedPlanning.Normalize(requested.RestaurantName) != AgentScopedPlanning.Normalize(dish.Restaurant.Name) ||
                requested.RestaurantId.HasValue && requested.RestaurantId != dish.RestaurantId ||
                requested.TagIds.Any(id => !tagSet.Contains(id)))
                errors.Add($"Món {item.DishId} không đáp ứng món/quán hoặc tag được yêu cầu cho ngày/bữa.");
            if (!confirm && requested.DifferentFromPlan && source?.Items.Any(old => old.DishId == item.DishId) == true &&
                (!unchanged || refreshed)) errors.Add($"Món {item.DishId} đã có trong lịch nguồn; cần phương án khác.");
            if (requested.DifferentFromPlan && snapshot.Items.Any(other => other.DishId == item.DishId &&
                (other.DayNumber != item.DayNumber || other.MealSlot != item.MealSlot)))
                errors.Add($"Món {item.DishId} bị lặp ở bữa khác trong lịch; chưa đáp ứng yêu cầu khác món.");
            if (!ProvinceSearch.Same(dish.Restaurant.ProvinceName, state.Province) ||
                state.ExcludeTagIds.Any(tagSet.Contains) || state.TagIds.Any(id => !tagSet.Contains(id)) ||
                state.MaxPricePerMeal.HasValue && dish.Price > state.MaxPricePerMeal ||
                AgentScopedPlanning.MealArea(state, item.DayNumber, item.MealSlot) is { } area && !area.Contains(dish.Restaurant.Latitude, dish.Restaurant.Longitude))
                errors.Add($"Món {item.DishId} không đáp ứng ràng buộc chuyến đi.");
            if (grant is not null && (!unchanged || refreshed))
            {
                var applied = grant.Constraints;
                if (AgentScopedPlanning.Fingerprint(applied, item.DayNumber, item.MealSlot) !=
                    AgentScopedPlanning.Fingerprint(state, item.DayNumber, item.MealSlot))
                    errors.Add($"Món {item.DishId} được lấy theo khẩu vị khác; cần lấy gợi ý lại.");
                if (applied.MaxPricePerMeal.HasValue && dish.Price > applied.MaxPricePerMeal || applied.ExcludeTagIds.Any(tagSet.Contains) || applied.TagIds.Any(id => !tagSet.Contains(id)) ||
                    AgentScopedPlanning.MealArea(applied, item.DayNumber, item.MealSlot) is { } issued && !issued.Contains(dish.Restaurant.Latitude, dish.Restaurant.Longitude))
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
                    if (!normalized.Any(x => x.DayNumber == day && x.MealSlot == meal) && state.SkippedMeals?.Contains(new(day, meal)) != true && !AlreadyEmpty(source, day, meal))
                        errors.Add($"Chưa có món cho bữa {meal} ngày {day}.");
        // Bữa sáng/trưa/tối mặc định cần một món chính. Chỉ kiểm tra bữa có món mới/được chọn lại trong lượt này
        // để lịch đã lưu trước quy tắc này vẫn sửa tiếp được; xác nhận lưu không áp lại quy tắc cho bản đã xem.
        var mainTag = allTags.FirstOrDefault(tag => tag.TagType == TagType.Category && tag.Code == AgentScopedPlanning.MainTagCode);
        if (!confirm && mainTag is not null)
            foreach (var (day, meal) in normalized.Where(x => changed.Contains(x.ItemKey)).Select(x => (x.DayNumber, x.MealSlot)).Distinct())
                if (AgentScopedPlanning.RequiresMain(state, day, meal, allTags) &&
                    !normalized.Any(x => x.DayNumber == day && x.MealSlot == meal && catalog[x.DishId].DishTags.Any(tag => tag.TagId == mainTag.Id)))
                    errors.Add($"Bữa {meal} ngày {day} cần ít nhất một món chính; đồ uống, tráng miệng và món ăn vặt chỉ là món phụ trừ khi người dùng yêu cầu riêng cho bữa đó.");
        var total = normalized.Sum(x => x.Price);
        if (state.BudgetAmount.HasValue && total > state.BudgetAmount) errors.Add(BudgetExceeded);
        var repeats = !confirm && errors.Count == 0 && freshGrants is { Count: > 0 }
            // Món dự phòng (chưa gửi cho model) cũng là lựa chọn thay thế hợp lệ, nên lặp vẫn tránh được khi grant chính đã dùng hết.
            ? await AvoidableRepeatsAsync(normalized, added, state, [.. freshGrants, .. spareGrants ?? []], catalog, allTags, mainTag, ct) : [];
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
    /// Brings an over-budget selection within budget by swapping its most expensive new dishes for cheaper dishes already ranked for
    /// the same position under the same constraints. Requested names and retained rows stay; returns null when the budget stays exceeded.
    /// </summary>
    public async Task<(AgentSnapshot Snapshot, int Replaced)?> RepairBudgetAsync(AgentSnapshot snapshot, AgentSnapshot? source,
        IReadOnlyList<AgentCandidateGrant> grants, CancellationToken ct)
    {
        var state = snapshot.State;
        if (state.BudgetAmount is not { } budget) return null;
        var scope = snapshot.Scope ?? new();
        var sourceItems = source?.Items ?? [];
        var targets = snapshot.Items.Where(item => scope.Contains(item) && !sourceItems.Any(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId) &&
            AgentScopedPlanning.Request(state, item.DayNumber, item.MealSlot) is { DishName: null, RestaurantName: null }).OrderByDescending(item => item.Price).ToArray();
        // Giữ thứ tự xếp hạng của Recommendation trong từng grant để vẫn ưu tiên món hợp khẩu vị nhất trong số món rẻ hơn.
        var options = targets.ToDictionary(item => item.ItemKey, item => grants
            .Where(grant => (!grant.DayNumber.HasValue || grant.DayNumber == item.DayNumber) && (grant.MealSlot is null || grant.MealSlot == item.MealSlot) &&
                AgentScopedPlanning.Fingerprint(grant.Constraints, item.DayNumber, item.MealSlot) == AgentScopedPlanning.Fingerprint(state, item.DayNumber, item.MealSlot))
            .SelectMany(grant => grant.DishIds).Distinct().ToArray());
        var catalog = (await dishes.GetCardDishesAsync(options.Values.SelectMany(ids => ids).Concat(snapshot.Items.Select(item => item.DishId)).Distinct().ToArray(), ct))
            .ToDictionary(dish => dish.Id);
        var usedNames = snapshot.Items.Where(item => catalog.ContainsKey(item.DishId))
            .Select(item => AgentScopedPlanning.Normalize(catalog[item.DishId].Name)!).ToHashSet(StringComparer.Ordinal);
        var total = snapshot.Items.Sum(item => item.Price);
        var replacements = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in targets)
        {
            if (total <= budget) break;
            var cheaper = options[item.ItemKey].Where(id => catalog.TryGetValue(id, out var dish) && IsPublic(dish) && FitsMeal(dish, item.MealSlot) &&
                dish.Price < item.Price && !usedNames.Contains(AgentScopedPlanning.Normalize(dish.Name)!)).Select(id => catalog[id]).ToArray();
            if (cheaper.Length == 0) continue;
            var pick = cheaper.FirstOrDefault(dish => total - item.Price + dish.Price <= budget) ?? cheaper.MinBy(dish => dish.Price)!;
            replacements[item.ItemKey] = pick.Id;
            usedNames.Add(AgentScopedPlanning.Normalize(pick.Name)!);
            total += pick.Price - item.Price;
        }
        return total > budget || replacements.Count == 0 ? null : (WithReplacements(snapshot, grants, replacements), replacements.Count);
    }

    /// <summary>Applies dish replacements; price, name and provenance are recalculated by validation, and grants shrink to the dishes kept.</summary>
    private static AgentSnapshot WithReplacements(AgentSnapshot snapshot, IReadOnlyList<AgentCandidateGrant> grants, IReadOnlyDictionary<string, int> replacements)
    {
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
