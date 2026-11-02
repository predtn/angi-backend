using System.Diagnostics;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Executes allowlisted read-only tools using backend-owned identity, catalog and scoped provenance.</summary>
public sealed class AgentToolExecutor(IDishRepository dishes, ITagRepository tags, IRecommendationService recommendation,
    AgentStatePolicy statePolicy, AgentRouteEstimator routes, AgentLimits limits, IAgentTelemetry telemetry,
    AgentPreferenceService? preferences = null, IAgentCatalogRepository? catalogSearch = null)
{
    /// <summary>Rejects unrecognized tools and validates arguments before executing any external operation.</summary>
    public async Task<JsonElement> ExecuteAsync(AgentContext context, AgentToolCall call, CancellationToken ct)
    {
        // Chỉ thực thi sau khi use case/ContextBuilder đã kiểm tra conversation và roadmap thuộc user.
        // Model chỉ đề nghị tên tool + arguments; backend quyết định có cho chạy hay không.
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Kiểm tra allowlist/schema cả khi provider đã trả HTTP thành công:
            // thiếu/thừa/trùng trường, sai kiểu hoặc tên tool lạ đều bị chặn trước thao tác bên ngoài.
            if (!AgentLlmSchema.Tools.TryGetValue(call.Name, out var schema) || !AgentLlmSchema.IsValid(call.Arguments, schema))
                throw Invalid("Cấu trúc tham số công cụ không hợp lệ.");
            // Tools cung cấp dữ liệu để LLM lên lịch, không tool nào được ghi roadmap.
            // Recommendation có thể có log riêng theo hợp đồng dịch vụ hiện có.
            object result = call.Name switch
            {
                "search_catalog" => await SearchAsync(context, AgentJson.Read<AgentCatalogInput>(call.Arguments.GetRawText()), ct),
                "get_recommendation" => await RecommendAsync(context, AgentJson.Read<AgentRecommendationInput>(call.Arguments.GetRawText()), ct),
                "estimate_day_route" => await RouteAsync(context, AgentJson.Read<AgentRouteInput>(call.Arguments.GetRawText()), ct),
                "get_roadmap" => GetRoadmap(context, AgentJson.Read<AgentRoadmapInput>(call.Arguments.GetRawText())),
                _ => throw Invalid("Agent yêu cầu công cụ không được phép.")
            };
            // Kết quả có cấu trúc được Orchestrator đưa lại cho LLM trong tool exchange kế tiếp.
            return JsonSerializer.SerializeToElement(result, AgentJson.Options);
        }
        catch (JsonException) { throw Invalid("Tham số công cụ không hợp lệ."); }
        finally
        {
            telemetry.Record("tool", context.ConversationId, context.TurnId, context.AttemptId, new { call.Name, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
    }

    /// <summary>Filters before ranking, validates the entire reco response and issues scoped backend grants.</summary>
    private async Task<object> RecommendAsync(AgentContext context, AgentRecommendationInput input, CancellationToken ct)
    {
        // Model có thể bổ sung state để lấy món nhưng không được tự đổi/nới yêu cầu đã chốt.
        // userId luôn lấy từ context đã xác thực, không phải tham số do model cung cấp.
        if (!context.ReplanAfterConfirmation)
            context.State = statePolicy.Accept(context.State, input.State, context.ConstraintBaseline);
        var state = context.State;
        var scope = new AgentScope(input.DayNumber, input.MealSlot);
        AgentProposalBuilder.ValidateScope(context, scope);
        // full = cả chuyến; day = một ngày; slot = một bữa của một ngày.
        // Chỉ ranking khi đã rõ tỉnh, số ngày và ngân sách (hoặc người dùng đã chấp nhận không giới hạn).
        // Kiểm tra thêm giá VND, bán kính, số ID và topK để không vượt hợp đồng Recommendation.
        if (input.Scope is not ("full" or "day" or "slot") || input.Scope == "full" && (input.DayNumber.HasValue || input.MealSlot is not null) ||
            input.Scope is "day" or "slot" && !input.DayNumber.HasValue || input.Scope == "day" && input.MealSlot is not null || input.Scope == "slot" && input.MealSlot is null ||
            string.IsNullOrWhiteSpace(state.Province) || !state.NumDays.HasValue ||
            state.BudgetAmount is null && !state.BudgetUnrestricted ||
            input.Province is not null && !string.Equals(input.Province.Trim(), state.Province.Trim(), StringComparison.OrdinalIgnoreCase) ||
            input.TopK < 1 || input.TopK > limits.MaxRanked || input.ExcludeDishIds is null || input.ExcludeDishIds.Length > 2000 || input.ExcludeDishIds.Any(x => x <= 0) ||
            input.TagIds is null || input.ExcludeTagIds is null || input.TagIds.Length > 100 || input.ExcludeTagIds.Length > 100 ||
            input.TagIds.Any(x => x <= 0) || input.ExcludeTagIds.Any(x => x <= 0) ||
            input.MaxPricePerMeal.HasValue && (input.MaxPricePerMeal < 0 || decimal.Truncate(input.MaxPricePerMeal.Value) != input.MaxPricePerMeal || input.MaxPricePerMeal > AgentStateValidator.MaxMoney) ||
            input.RadiusKm.HasValue && (input.RadiusKm < 1 || input.RadiusKm > 50 || state.OriginLatitude is null))
            throw Invalid("Cần làm rõ tỉnh, ngân sách hoặc phạm vi trước khi lấy gợi ý.");
        var catalogTags = await tags.GetTagsAsync(null, ct);
        var alternative = context.Request.Command == "alternative" || input.Alternative;
        if (alternative && !context.AlternativeScopes.Contains(scope)) context.AlternativeScopes.Add(scope);
        // Ngày/bữa có khẩu vị hoặc yêu cầu món khác nhau được backend tự tách thành các phạm vi đồng nhất ngay trong lượt gọi này.
        // Model chỉ cần một lần gọi cho cả phạm vi; mỗi phần có vector, bộ lọc và grant riêng nên không lẫn ứng viên giữa các bữa.
        var parts = HomogeneousScopes(state, scope);
        if (parts.Length > 1)
            telemetry.Record("scope_split", context.ConversationId, context.TurnId, context.AttemptId, new { parts = parts.Length });
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var rankings = new List<ScopedRanking>();
        foreach (var part in parts)
        {
            var ranking = await RankScopeAsync(context, input, scope, part, catalogTags, alternative, usedNames, ct);
            if (ranking.Notice is { } notice) return notice;
            rankings.Add(ranking);
        }
        // Không có món phù hợp ở mọi bữa: trả danh sách rỗng/cảnh báo để model hỏi lại, không tự nới điều kiện.
        if (rankings.All(x => x.RequestIds.Count == 0))
            return Notice(context, rankings.Any(x => x.Coverage.Length > 0) ? AgentReplyBuilder.Coverage(context)
                : "Chưa tìm thấy món khả dụng đáp ứng các điều kiện hiện tại. Bạn có muốn điều chỉnh điều kiện hoặc chọn bữa khác không?", "no_candidates");
        var coverage = rankings.SelectMany(x => x.Coverage).ToArray();
        var returned = rankings.SelectMany(x => x.Picked).DistinctBy(x => x.Id).Count();
        context.ToolNotice = returned == 0
            ? (coverage.Length > 0 ? AgentReplyBuilder.Coverage(context)
                : "Chưa có kết quả gợi ý khả dụng cho phạm vi này. Bạn có muốn điều chỉnh điều kiện không?")
            : null;
        var personalized = rankings.SelectMany(x => x.RequestIds).All(id => id.HasValue);
        var coverageWarning = coverage.Any(meal => meal.AvailableCount == 0)
            ? "Có bữa chưa có ứng viên phù hợp trong kết quả này. Kiểm tra mealCoverage và lấy gợi ý riêng cho bữa đó trước khi kết luận thiếu món."
            : rankings.Any(x => !x.PerMeal && x.ExpectedMeals > x.Picked.Count) ? "Có thể chưa đủ lựa chọn cho các bữa; cần lấy thêm gợi ý hoặc hỏi người dùng, không tự nới điều kiện." : null;
        var warnings = personalized ? Array.Empty<string>() : ["Đang dùng thứ tự đánh giá thay cho cá nhân hóa."];
        var candidateCount = rankings.Sum(x => x.CandidateCount);
        var ignoredCount = rankings.Sum(x => x.IgnoredCount);
        var mealCoverage = coverage.Select(x => new { x.DayNumber, x.MealSlot, x.AvailableCount, x.RequiredTags });
        // Ngân sách chia đều cho các bữa: mốc giá để model chọn món vừa túi tiền, không phải bộ lọc (một bữa có thể đắt hơn nếu bữa khác rẻ hơn).
        var budgetPerMeal = AgentScopedPlanning.BudgetPerMeal(state);
        // Payload gọn cho LLM: tọa độ do backend tự tra khi tính tuyến, bữa ăn nằm ở allowedMealSlots nên tags chỉ giữ mã loại món.
        // Giữ thông báo fallback để model không mô tả thứ tự theo đánh giá là ranking cá nhân hóa.
        if (rankings.Count == 1)
            return new
            {
                rankingMode = personalized ? "personalized" : "rating_fallback",
                selectionScope = new { scope.DayNumber, scope.MealSlot },
                candidateCount, ignoredCount, returnedCount = returned, mealCoverage, coverageWarning, warnings, budgetPerMeal,
                items = Items(rankings[0])
            };
        // Phạm vi đã tách: mỗi scopedResults có selectionScope riêng, món chỉ được dùng đúng ngày/bữa của phần đó.
        return new
        {
            rankingMode = personalized ? "personalized" : "rating_fallback",
            selectionScope = new { scope.DayNumber, scope.MealSlot },
            candidateCount, ignoredCount, returnedCount = returned, mealCoverage, coverageWarning, warnings, budgetPerMeal,
            scopedResults = rankings.Select(x => new { selectionScope = new { x.Scope.DayNumber, x.Scope.MealSlot }, items = Items(x) })
        };
    }

    /// <summary>Splits a broad scope into day scopes, or meal scopes of a day, whose positions share one taste and meal request.</summary>
    private static AgentScope[] HomogeneousScopes(AgentState state, AgentScope scope)
    {
        if (IsHomogeneous(state, scope)) return [scope];
        var days = scope.DayNumber is { } only ? [only] : Enumerable.Range(1, state.NumDays ?? 0).Select(day => (short)day).ToArray();
        var meals = state.MealSlots.Length > 0 ? AgentStateValidator.Meals.Where(state.MealSlots.Contains).ToArray() : AgentStateValidator.Meals;
        return days.SelectMany(day => IsHomogeneous(state, new(day, null)) ? [new AgentScope(day, null)]
            : meals.Select(meal => new AgentScope(day, meal))).ToArray();
    }

    /// <summary>Compares only positions this scope actually searches: declared meals, or every meal for legacy state.</summary>
    private static bool IsHomogeneous(AgentState state, AgentScope scope) => AgentScopedPlanning.Slots(state, scope)
        .Where(x => state.MealSlots.Length == 0 || state.MealSlots.Contains(x.Meal) || scope.MealSlot == x.Meal)
        .Select(x => AgentScopedPlanning.Fingerprint(state, x.Day, x.Meal)).Distinct().Skip(1).Any() is false;

    /// <summary>Projects ranked dishes with the meals their grant authorizes, without coordinates or meal-time tags.</summary>
    private static IEnumerable<object> Items(ScopedRanking ranking) => ranking.Picked.Select(x => (object)new
    {
        dishId = x.Id, x.Name, x.Price, restaurantName = x.Restaurant.Name,
        allowedMealSlots = ranking.PerMeal
            ? AgentStateValidator.Meals.Where(slot => ranking.ChosenByMeal.GetValueOrDefault(slot)?.Any(d => d.Id == x.Id) == true).ToArray()
            : AgentStateValidator.Meals.Where(slot => (ranking.Scope.MealSlot is null || ranking.Scope.MealSlot == slot) && FitsMeal(x, slot)).ToArray(),
        tags = x.DishTags.Where(t => t.Tag.TagType != ANGI.Domain.Enums.TagType.MealTime).Select(t => t.Tag.Code).ToArray()
    });

    /// <summary>Ranks one homogeneous scope and issues its grants; exclusions follow the scope the model requested.</summary>
    private async Task<ScopedRanking> RankScopeAsync(AgentContext context, AgentRecommendationInput input, AgentScope requested,
        AgentScope scope, IReadOnlyCollection<ANGI.Domain.Entities.Tag> catalogTags, bool alternative, HashSet<string> usedNames, CancellationToken ct)
    {
        var state = context.State;
        var slots = AgentScopedPlanning.Slots(state, scope).ToArray();
        var (day, meal) = slots[0];
        var mealRequest = AgentScopedPlanning.Request(state, day, meal);
        // Tag phải có thật trong catalog. Gộp các bộ lọc theo hướng giữ/siết ràng buộc state.
        var tagIds = state.TagIds.Concat(input.TagIds!).Concat(mealRequest.TagIds).Distinct().ToArray();
        var excludeTags = state.ExcludeTagIds.Concat(input.ExcludeTagIds!).Distinct().ToArray();
        if (tagIds.Concat(excludeTags).Except(catalogTags.Select(x => x.Id)).Any()) throw Invalid("Bộ lọc chứa tag không có trong catalog.");
        // Khi thay món, tránh chọn lại món đang giữ ở ngoài scope model yêu cầu.
        // Với command alternative, loại cả món cũ trong scope để phương án mới khác bản trước.
        var excluded = (context.Source?.Items ?? []).Where(item => !requested.Contains(item) || alternative)
            .Select(x => x.DishId).Concat(input.ExcludeDishIds!).Distinct().ToArray();
        if (mealRequest.DifferentFromPlan)
            excluded = excluded.Concat((context.Source?.Items ?? []).Select(x => x.DishId)).Distinct().ToArray();
        long? restaurantId = null;
        int[] namedDishIds = [];
        if (mealRequest.RestaurantName is { } restaurantName)
        {
            var lookup = await LookupAsync(context, new("restaurant", restaurantName, state.Province, mealRequest.RestaurantId), ct);
            if (lookup.Status != "found") return ScopedRanking.Stopped(scope, Notice(context, Describe(lookup), lookup.Status));
            restaurantId = lookup.Matches.Single().RestaurantId;
        }
        if (mealRequest.DishName is { } dishName)
        {
            var lookup = await LookupAsync(context, new("dish", dishName, state.Province), ct);
            if (lookup.Status != "found") return ScopedRanking.Stopped(scope, Notice(context, Describe(lookup), lookup.Status));
            namedDishIds = lookup.Matches.Where(x => x.Available && (!restaurantId.HasValue || x.RestaurantId == restaurantId))
                .Select(x => checked((int)x.Id)).ToArray();
            if (namedDishIds.Length == 0)
                return ScopedRanking.Stopped(scope, Notice(context, "Chưa tìm thấy món được yêu cầu trong menu khả dụng của nhà hàng đã chọn. Bạn có muốn chọn món hoặc nhà hàng khác không?", "menu_mismatch"));
        }
        // Hai giới hạn cùng tồn tại thì lấy giá trị nhỏ hơn: tool không được tăng trần giá/bán kính của state.
        var maxPrice = Min(state.MaxPricePerMeal, input.MaxPricePerMeal);
        var radius = Min(state.RadiusKm, input.RadiusKm);
        var area = radius is { } km && state.OriginLatitude is { } lat && state.OriginLongitude is { } lng ? new NearbyArea(lat, lng, km) : null;
        var requiredTags = catalogTags.Where(tag => tagIds.Contains(tag.Id)).Select(tag => tag.Name).ToArray();
        // Tìm cả chuyến/cả ngày với các bữa đã khai báo: xếp hạng RIÊNG từng bữa để bữa sáng không bị món trưa/tối
        // có điểm cao chiếm hết topK. Mỗi bữa có suất (số ngày trong phạm vi + 2) món khác tên, đủ cho một lượt
        // lập lịch mà model không cần thêm vòng tool cho từng bữa. Phạm vi slot hoặc lịch cũ không khai báo bữa giữ một lần xếp hạng.
        var perMealSearch = scope.MealSlot is null && state.MealSlots.Length > 0;
        string?[] meals = perMealSearch
            ? AgentStateValidator.Meals.Where(slot => state.MealSlots.Contains(slot) && slots.Any(x => x.Meal == slot)).ToArray<string?>()
            : [scope.MealSlot];
        var mealQuota = perMealSearch ? Math.Min(input.TopK, slots.Select(x => x.Day).Distinct().Count() + 2) : input.TopK;
        // Phạm vi đã bị tách thành nhiều phần (ví dụ ngày 2 và ngày 4 cùng khẩu vị) thì mỗi phần có cùng vector và ứng viên,
        // nên ranking giống hệt nhau. Các phần sau ưu tiên tên chưa dùng ở phần trước để các ngày không lặp lại cùng món.
        var diversify = perMealSearch || requested != scope;
        // Món được yêu cầu theo tên có thể có ở nhiều chi nhánh: giữ tất cả để model hỏi chọn chi nhánh khi cần.
        var distinctNames = namedDishIds.Length == 0;
        var mainTag = catalogTags.FirstOrDefault(tag => tag.TagType == ANGI.Domain.Enums.TagType.Category && tag.Code == AgentScopedPlanning.MainTagCode);
        var chosenByMeal = new Dictionary<string, ANGI.Domain.Entities.Dish[]>();
        var picked = new List<ANGI.Domain.Entities.Dish>();
        var requestIds = new List<Guid?>();
        int candidateTotal = 0, ignoredTotal = 0;
        foreach (var mealSlot in meals)
        {
            // Bữa sáng/trưa/tối mặc định chỉ xếp hạng món chính, trừ khi người dùng đã chọn loại món/món/quán cho bữa đó
            // (ví dụ "bữa sáng chỉ đồ uống") hoặc model đang tìm món phụ bằng tagIds loại món.
            var mealTags = mainTag is not null && mealSlot is not null && !tagIds.Contains(mainTag.Id) &&
                AgentScopedPlanning.RequiresMain(state, slots.First(x => x.Meal == mealSlot).Day, mealSlot, catalogTags, input.TagIds)
                ? tagIds.Append(mainTag.Id).ToArray() : tagIds;
            var filter = new DishCandidateFilter { Province = state.Province, TagIds = mealTags, ExcludeTagIds = excludeTags,
                ExcludeDishIds = excluded, MaxPrice = maxPrice, Area = area, RestaurantId = restaurantId, DishIds = namedDishIds,
                RequireAllTags = true, CompatibleMealTimeCode = mealSlot };
            // Backend lọc catalog TRƯỚC, chỉ gửi tối đa MaxCandidates IDs hợp lệ sang Recommendation.
            // Recommendation xếp hạng tập được gửi; không tự tìm món từ schema core hoặc gọi ngược backend.
            // Catalog có rất nhiều bản cùng tên ở nhiều quán: lấy rộng rồi gom theo tên, giữ quán đứng đầu thứ tự rating,
            // để reco xếp hạng các món khác nhau thay vì hàng trăm bản sao của vài món.
            var pool = await dishes.GetCandidateIdsAsync(filter, Math.Max(limits.MaxCandidates, CandidatePool), ct);
            var cards = pool.Count == 0 ? new Dictionary<int, ANGI.Domain.Entities.Dish>()
                : (await dishes.GetCardDishesAsync(pool.ToArray(), ct)).ToDictionary(dish => dish.Id);
            // Discovery gộp tag cùng loại bằng OR; ràng buộc Agent cần món thỏa TẤT CẢ tag được yêu cầu.
            var qualified = pool.Where(cards.ContainsKey).Select(id => cards[id])
                .Where(dish => mealTags.All(id => dish.DishTags.Any(tag => tag.TagId == id)) && FitsMeal(dish, mealSlot));
            if (distinctNames) qualified = qualified.DistinctBy(dish => AgentScopedPlanning.Normalize(dish.Name));
            IReadOnlyList<int> candidates = qualified.Select(dish => dish.Id).Take(limits.MaxCandidates).ToArray();
            candidateTotal += candidates.Count;
            // Bữa không có món phù hợp được ghi nhận qua mealCoverage = 0; không tự nới điều kiện.
            if (candidates.Count == 0)
            {
                if (mealSlot is not null) chosenByMeal[mealSlot] = [];
                continue;
            }
            if (requestIds.Count == 0)
                context.State = state = await (preferences ?? new AgentPreferenceService(recommendation)).EnsureAsync(context.State, context.UserId, ct);
            var vectorSlot = slots.First(x => mealSlot is null || x.Meal == mealSlot);
            // Xin dư để còn đủ món sau khi bỏ trùng tên; reco chỉ tính lượt hiển thị qua /impressions khi lịch được lưu.
            // Khi đa dạng hóa, xin thêm đúng số tên đã dùng: sau khi đẩy các tên đó xuống cuối vẫn còn đủ món mới cho suất của bữa.
            var topK = perMealSearch ? Math.Min(limits.MaxRanked, mealQuota * 3) : input.TopK;
            if (diversify) topK = Math.Min(limits.MaxRanked, topK + usedNames.Count);
            // ĐÂY LÀ LỜI GỌI RECOMMENDATION (RECO-05), khác với lời gọi LLM ở AgentOrchestrator.
            // IRecommendationService dùng cấu hình/kết nối riêng; API key LLM không truyền qua tool này.
            var ranked = state.Preferences?.Initialized == true
                ? await recommendation.RecommendConversationAsync(context.UserId, candidates, topK,
                    new RecommendationVector("conversation", AgentScopedPlanning.Vector(state, vectorSlot.Day, vectorSlot.Meal)), ct)
                : new RecommendationResult(null, candidates.Take(topK).ToArray(), []);
            if (ranked.RequestId.HasValue && ranked.VectorSource != "conversation")
                ranked = new RecommendationResult(null, candidates.Take(topK).ToArray(), []);
            var allowed = candidates.ToHashSet();
            // Không tin ID/score chỉ vì request thành công: IDs phải thuộc input, số lượng không vượt topK,
            // score phải hữu hạn và khớp số món. Personalized có requestId; rating fallback không có score/requestId.
            if (ranked.DishIds.Count > topK || ranked.DishIds.Any(id => !allowed.Contains(id)) ||
                ranked.IgnoredDishIds.Any(id => !allowed.Contains(id)) || ranked.RequestId == Guid.Empty ||
                ranked.RequestId.HasValue && (ranked.Scores is null || ranked.Scores.Count != ranked.DishIds.Count || ranked.Scores.Any(x => !double.IsFinite(x))) ||
                !ranked.RequestId.HasValue && ranked.Scores is not null)
                throw Invalid("Dịch vụ gợi ý trả kết quả không hợp lệ.");
            requestIds.Add(ranked.RequestId);
            ignoredTotal += ranked.IgnoredDishIds.Count;
            // Loại ID trùng nhưng giữ thứ tự ranking; lấy metadata từ catalog backend thay vì để LLM tự bịa.
            var rankedIds = ranked.DishIds.Distinct().ToArray();
            var catalog = (await dishes.GetCardDishesAsync(rankedIds, ct)).ToDictionary(x => x.Id);
            var actual = rankedIds.Where(catalog.ContainsKey).Select(id => catalog[id])
                .Where(d => string.Equals(d.Restaurant.ProvinceName?.Trim(), state.Province!.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    FitsMeal(d, mealSlot) && (!restaurantId.HasValue || d.RestaurantId == restaurantId) &&
                    (namedDishIds.Length == 0 || namedDishIds.Contains(d.Id)) && mealTags.All(id => d.DishTags.Any(t => t.TagId == id))).ToArray();
            // Catalog có cùng một món ở nhiều quán với cùng vector nên điểm gần như bằng nhau: chỉ giữ bản xếp hạng cao nhất
            // của mỗi tên. Khi chia theo bữa, ưu tiên tên chưa dùng ở bữa trước (OrderBy ổn định nên vẫn giữ thứ tự ranking).
            var distinct = distinctNames ? actual.DistinctBy(d => AgentScopedPlanning.Normalize(d.Name)).ToArray() : actual;
            var chosen = distinct;
            if (diversify)
                chosen = chosen.OrderBy(d => usedNames.Contains(AgentScopedPlanning.Normalize(d.Name)!) ? 1 : 0)
                    .Take(perMealSearch ? mealQuota : input.TopK).ToArray();
            // Chỉ ghi nhận các món đầu, mỗi vị trí (ngày) của bữa một món, vì đó là món gần như chắc được chọn;
            // ghi cả danh sách thì phần sau bị đẩy xuống món xếp hạng thấp hoặc hết món mới.
            var likelyPicks = mealSlot is null ? chosen : chosen.Take(slots.Count(x => x.Meal == mealSlot));
            usedNames.UnionWith(likelyPicks.Select(d => AgentScopedPlanning.Normalize(d.Name)!));
            if (mealSlot is not null) chosenByMeal[mealSlot] = chosen;
            picked.AddRange(chosen.Where(d => picked.All(x => x.Id != d.Id)));
            // Grant là bằng chứng backend đã cấp các món này cho đúng ngày/bữa và điều kiện lọc.
            // Validator dựa vào grant để chặn model thêm/thay bằng ID chưa được cấp hoặc sai scope.
            var constraints = state with { TagIds = mealTags, ExcludeTagIds = excludeTags, MaxPricePerMeal = maxPrice, RadiusKm = radius };
            context.Grants.Add(new(scope.DayNumber, mealSlot, chosen.Select(x => x.Id).ToArray(), ranked.RequestId, constraints));
            // Phần còn lại của cùng danh sách reco (cùng request, cùng vector) không gửi cho model. Khi một ngày chỉ được cấp vài món
            // và model chọn trùng món đã dùng, backend lấy món chưa dùng xếp hạng cao nhất ở đây để thay, không tốn thêm lần gọi LLM.
            var reserve = distinct.Where(d => !chosen.Contains(d)).Select(x => x.Id).ToArray();
            if (reserve.Length > 0) context.ReserveGrants.Add(new(scope.DayNumber, mealSlot, reserve, ranked.RequestId, constraints));
            telemetry.Record("ranking", context.ConversationId, context.TurnId, context.AttemptId, new { mode = ranked.RequestId.HasValue ? "personalized" : "rating_fallback",
                candidateCount = candidates.Count, ranked.RequestId, mealSlot, returnedCount = chosen.Length });
        }
        // Bữa không có món vẫn được ghi mealCoverage = 0 để model/reply dựa trên bằng chứng thay vì đoán thiếu món.
        var coverage = RecordCoverage(context, state, scope, slot => perMealSearch
            ? chosenByMeal.GetValueOrDefault(slot)?.Length ?? 0 : picked.Count(dish => FitsMeal(dish, slot)), requiredTags);
        return new(scope, perMealSearch, chosenByMeal, picked, requestIds, candidateTotal, ignoredTotal, coverage,
            slots.Count(x => state.MealSlots.Contains(x.Meal)));
    }

    // RECO-05 nhận tối đa 2000 ứng viên; lấy rộng tới mức này trước khi gom theo tên rồi cắt theo Agent:MaxCandidates.
    private const int CandidatePool = 2000;

    /// <summary>Ranking outcome of one homogeneous scope, or the trusted notice that stops the whole tool call.</summary>
    private sealed record ScopedRanking(AgentScope Scope, bool PerMeal, Dictionary<string, ANGI.Domain.Entities.Dish[]> ChosenByMeal,
        List<ANGI.Domain.Entities.Dish> Picked, List<Guid?> RequestIds, int CandidateCount, int IgnoredCount,
        AgentMealCoverage[] Coverage, int ExpectedMeals, object? Notice = null)
    {
        /// <summary>Wraps a catalog clarification so no partial candidates are returned with it.</summary>
        public static ScopedRanking Stopped(AgentScope scope, object notice) => new(scope, false, [], [], [], 0, 0, [], 0, notice);
    }

    /// <summary>Counts candidates compatible with each requested meal and replaces earlier observations for that slot.</summary>
    private static AgentMealCoverage[] RecordCoverage(AgentContext context, AgentState state, AgentScope scope,
        Func<string, int> available, string[] requiredTags)
    {
        var coverage = AgentScopedPlanning.Slots(state, scope)
            .Where(slot => state.MealSlots.Contains(slot.Meal) || scope.MealSlot == slot.Meal)
            .Select(slot => new AgentMealCoverage(slot.Day, slot.Meal, available(slot.Meal),
                AgentScopedPlanning.Fingerprint(state, slot.Day, slot.Meal), requiredTags)).ToArray();
        foreach (var meal in coverage)
        {
            context.MealCoverage.RemoveAll(previous => previous.DayNumber == meal.DayNumber && previous.MealSlot == meal.MealSlot);
            context.MealCoverage.Add(meal);
        }
        return coverage;
    }

    private async Task<object> SearchAsync(AgentContext context, AgentCatalogInput input, CancellationToken ct)
    {
        var result = await LookupAsync(context, input, ct);
        context.ToolNotice = Describe(result);
        return new { result.Status, result.Message, result.Matches };
    }

    private async Task<CatalogLookup> LookupAsync(AgentContext context, AgentCatalogInput input, CancellationToken ct)
    {
        if (input.Kind is not ("dish" or "restaurant") || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200 ||
            input.Province is not null && (string.IsNullOrWhiteSpace(input.Province) || input.Province.Length > 100) || input.SelectedId is <= 0)
            throw Invalid("Tên món/quán cần tra cứu không hợp lệ.");
        if (catalogSearch is null) throw Invalid("Tra cứu catalog chưa được cấu hình.");
        var matches = await catalogSearch.SearchAsync(input.Kind, input.Name, input.Province, 21, ct);
        var exact = matches.Where(x => AgentScopedPlanning.Normalize(x.Name) == AgentScopedPlanning.Normalize(input.Name)).ToArray();
        if (input.SelectedId.HasValue) exact = exact.Where(x => x.Id == input.SelectedId).ToArray();
        if (input.SelectedId.HasValue && exact.Length == 0)
            return new("ambiguous", "Lựa chọn chưa khớp kết quả tra cứu đúng tên. Bạn muốn chọn kết quả nào trong danh sách?", matches.Take(20).ToArray());
        if (exact.Length == 0)
        {
            var elsewhere = input.Province is null ? [] : await catalogSearch.SearchAsync(input.Kind, input.Name, null, 21, ct);
            var outside = elsewhere.Any(x => AgentScopedPlanning.Normalize(x.Name) == AgentScopedPlanning.Normalize(input.Name));
            return new(outside ? "outside_province" : matches.Length > 0 ? "ambiguous" : "not_found",
                outside ? "Có kết quả cùng tên trong hệ thống nhưng ngoài tỉnh đã chọn. Bạn muốn giữ địa điểm hay thay đổi?"
                : matches.Length > 0 ? "Chưa xác định được đúng món/quán theo tên đã yêu cầu. Bạn muốn chọn kết quả nào trong danh sách?"
                : "Chưa tìm thấy món/quán theo tên yêu cầu trong danh mục ANGI. Bạn có muốn xem lựa chọn khác không?", matches.Take(20).ToArray());
        }
        if (input.Kind == "restaurant" && exact.Length != 1 || exact.Length > 20)
            return new("ambiguous", "Có nhiều kết quả cùng tên. Bạn muốn chọn nhà hàng/chi nhánh nào trong danh sách?", exact.Take(20).ToArray());
        if (!exact.Any(x => x.Available))
            return new("unavailable", "Món/quán có trong hệ thống nhưng hiện chưa khả dụng. Bạn có muốn xem lựa chọn khác không?", exact);
        return new("found", "Đã tìm thấy kết quả đúng tên trong catalog.", exact);
    }

    private static object Notice(AgentContext context, string message, string status)
    {
        context.ToolNotice = message;
        return new { status, rankingMode = "none", requestId = (Guid?)null, items = Array.Empty<object>(), warnings = new[] { message } };
    }

    private static bool FitsMeal(ANGI.Domain.Entities.Dish dish, string? meal) => meal is null ||
        !dish.DishTags.Any(x => x.Tag.TagType == ANGI.Domain.Enums.TagType.MealTime) ||
        dish.DishTags.Any(x => x.Tag.TagType == ANGI.Domain.Enums.TagType.MealTime && x.Tag.Code == meal);

    private sealed record CatalogLookup(string Status, string Message, AgentCatalogMatch[] Matches);

    private static string Describe(CatalogLookup lookup) => lookup.Message + (lookup.Matches.Length == 0 ? "" : "\n" +
        string.Join("\n", lookup.Matches.Take(4).Select(x => $"{x.Name} — {x.RestaurantName}, {x.Address}, {x.Province}; " +
            (x.Available ? "khả dụng" : "chưa khả dụng"))));

    /// <summary>Resolves every coordinate from issued candidate IDs or unchanged source items.</summary>
    private async Task<object> RouteAsync(AgentContext context, AgentRouteInput input, CancellationToken ct)
    {
        // Preview chưa có roadmap/day ID nên nhận danh sách itemKey + dishId theo thứ tự muốn đi.
        // Tối đa 24 món để còn chỗ cho origin trong giới hạn 25 điểm của route adapter.
        if (!context.State.NumDays.HasValue || input.DayNumber < 1 || input.DayNumber > context.State.NumDays || input.OrderedItems is null || input.OrderedItems.Length > 24 ||
            input.OrderedItems.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemKey) || x.ItemKey.Length > 100 || x.DishId <= 0) ||
            input.OrderedItems.Select(x => x.ItemKey).Distinct().Count() != input.OrderedItems.Length || input.Profile is not ("driving" or "walking" or "cycling"))
            throw Invalid("Tham số tuyến không hợp lệ.");
        // Chỉ nhận ID từ candidates đã cấp cho ngày này hoặc từ các món nguồn thuộc ngày đó.
        // Không cho model gửi tọa độ tùy ý; tọa độ luôn tra từ quán của dishId trong catalog.
        var allowed = context.Grants.Where(g => !g.DayNumber.HasValue || g.DayNumber == input.DayNumber).SelectMany(g => g.DishIds)
            .Concat((context.Source?.Items ?? []).Where(x => x.DayNumber == input.DayNumber).Select(x => x.DishId)).ToHashSet();
        if (input.OrderedItems.Any(x => !allowed.Contains(x.DishId))) throw Invalid("Tuyến chứa món chưa được backend cấp.");
        var catalog = (await dishes.GetCardDishesAsync(input.OrderedItems.Select(x => x.DishId).Distinct().ToArray(), ct)).ToDictionary(x => x.Id);
        if (input.OrderedItems.Any(x => !catalog.ContainsKey(x.DishId))) throw Invalid("Món trong tuyến không còn khả dụng.");
        // "lunch" chỉ là giá trị tạm để dựng AgentItem cho route; thứ tự chặng theo OrderedItems.
        // Không dùng giá trị bữa tạm này để thay lịch proposal hoặc ghi roadmap.
        var items = input.OrderedItems.Select((x, i) => new AgentItem(x.ItemKey, null, input.DayNumber, "lunch", (short)i, x.DishId,
            Latitude: catalog[x.DishId].Restaurant.Latitude, Longitude: catalog[x.DishId].Restaurant.Longitude)).ToArray();
        // Tái sử dụng RM-14 qua AgentRouteEstimator; trả rõ cờ estimate/origin bị bỏ qua nếu có.
        return await routes.EstimateAsync(context.State, input.DayNumber, items, ParseProfile(input.Profile), ct);
    }

    /// <summary>Returns only the roadmap already bound to and owned by the current conversation.</summary>
    // Roadmap đã được ContextBuilder nạp sau kiểm tra ownership. Tool chỉ trả đúng roadmap đó.
    // Viết "sửa roadmap 88" trong chat không tự gắn conversation vào 88; frontend phải mở đúng context.
    // Hiện chưa có tool liệt kê/tìm/chuyển sang roadmap khác chỉ bằng lời chat.
    private static object GetRoadmap(AgentContext context, AgentRoadmapInput input) =>
        input.RoadmapId > 0 && input.RoadmapId == context.Roadmap?.Id ? AgentJson.FromRoadmap(context.Roadmap, context.State)
            : throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");

    /// <summary>Combines optional upper bounds without relaxing an existing bound.</summary>
    // Một bên chưa đặt giới hạn thì dùng bên còn lại; cả hai có thì giữ mức chặt hơn.
    private static decimal? Min(decimal? left, decimal? right) => left.HasValue && right.HasValue ? Math.Min(left.Value, right.Value) : left ?? right;

    /// <summary>Parses a schema-checked route profile.</summary>
    // Input đã được kiểm tra chỉ nhận driving/walking/cycling; nhánh mặc định biểu diễn driving.
    private static RouteProfile ParseProfile(string profile) => profile switch { "walking" => RouteProfile.Walking, "cycling" => RouteProfile.Cycling, _ => RouteProfile.Driving };

    /// <summary>Returns a controlled Agent failure while preserving the current persisted proposal.</summary>
    // Tool không hoàn tất đúng hợp đồng thì dừng lượt; use case giữ proposal cũ để người dùng thử lại.
    private static ServiceUnavailableException Invalid(string message) => new("AGENT_UNAVAILABLE", message);
}

/// <summary>Allowlisted recommendation arguments; caller identity is deliberately absent.</summary>
public sealed record AgentRecommendationInput(AgentState? State = null, string? Province = null, string Scope = "full",
    short? DayNumber = null, string? MealSlot = null, short[]? TagIds = null, short[]? ExcludeTagIds = null,
    int[]? ExcludeDishIds = null, decimal? MaxPricePerMeal = null, decimal? RadiusKm = null, int TopK = 50, bool Alternative = false);

/// <summary>Ordered unsaved preview route request.</summary>
public sealed record AgentRouteInput(short DayNumber, AgentRouteItem[] OrderedItems, string Profile = "driving");

/// <summary>References a backend-issued dish using an opaque preview key.</summary>
public sealed record AgentRouteItem(string ItemKey, int DishId);

/// <summary>References the single owned roadmap attached to a conversation.</summary>
public sealed record AgentRoadmapInput(long RoadmapId);
