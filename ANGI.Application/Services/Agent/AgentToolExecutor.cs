using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.Services.Agent.Places;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Executes allowlisted read-only tools using backend-owned identity, catalog and scoped provenance.</summary>
public sealed class AgentToolExecutor(IDishRepository dishes, ITagRepository tags, IRecommendationService recommendation,
    AgentConstraintGuard statePolicy, AgentRouteEstimator routes, AgentLimits limits, IAgentTelemetry telemetry,
    AgentPreferenceService? preferences = null, IAgentCatalogRepository? catalogSearch = null, AgentPlaceResolver? places = null)
{
    // Thang điểm reco là 0..1. Đo trên yêu cầu thật của các tài khoản có vector khẩu vị: món hạng 2 kém món đầu 0,0003-0,06, hạng 5 kém 0,02-0,12.
    // Mức 0,05 gom khoảng 1-13 món đầu vào cùng một nhóm, còn chênh lệch lớn hơn vẫn do khẩu vị quyết định.
    public const double NearlyEqualScore = 0.05;
    private const int MaxPlaceName = 100;
    // Chuyến dài bị tách theo ngày có nhiều ngày cùng điều kiện: mỗi bữa của mỗi điều kiện chỉ lọc và xếp hạng một lần trong một lượt gọi tool.
    private readonly Dictionary<string, RankedMeal> _rankedMeals = [];

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
                "suggest_dish" => await SuggestResultAsync(context, AgentJson.Read<AgentSuggestionInput>(call.Arguments.GetRawText()), ct),
                "locate_places" => await LocatePlacesAsync(context, AgentJson.Read<AgentLocateInput>(call.Arguments.GetRawText()), ct),
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

    private static readonly HashSet<string> Fillers =
    [
        "cai", "mon", "do", "nay", "kia", "ay", "no", "di", "nhe", "nha", "giup", "dum", "toi", "minh", "em", "cho", "lai",
        "khac", "hay", "vay", "thoi", "luon", "a", "ha", "nhi", "duoc", "khong", "voi", "ban", "the"
    ];

    /// <summary>True when a name is only pointing words ("cái này", "món đó"), so it cannot be a catalog lookup.</summary>
    public static bool IsPointingOnly(string name)
    {
        var words = Regex.Split(NormalizePointing(name), @"[^a-z0-9]+").Where(word => word.Length > 0).ToArray();
        return words.Length > 0 && words.All(Fillers.Contains);
    }

    private static string NormalizePointing(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');

    /// <summary>Filters before ranking, validates the entire reco response and issues scoped backend grants.</summary>
    private async Task<object> RecommendAsync(AgentContext context, AgentRecommendationInput input, CancellationToken ct)
    {
        // STEP-01: Return the result of ArgumentError early when AgentConstraintGuard.UnbackedTastes() has items, otherwise when context.ReplanAfterConfirmation is false, set context.State.
        // Mức khẩu vị người dùng chưa nói thì chưa được xếp hạng: model phải hỏi lại, không được tự chọn một con số.
        if (AgentConstraintGuard.UnbackedTastes(context, input.State) is { Length: > 0 } unbacked)
            return ArgumentError(context, $"Mức của {string.Join(", ", unbacked)} chưa có nguyên văn người dùng trong levelQuote. Nếu họ chưa nêu mức, hãy hỏi lại mức (clarification=taste_intensity), hỏi luôn cả phạm vi (cả chuyến, một ngày hay một bữa) nếu họ cũng chưa nêu, không xếp hạng.");
        // Ngân sách mới chưa có nguyên văn người dùng thì chưa được dùng để chọn món.
        if (AgentConstraintGuard.HasUnbackedBudget(context, input.State))
            return ArgumentError(context, "budgetAmount cần budgetQuote là nguyên văn người dùng đã viết và đúng cỡ số tiền họ nêu. Nếu họ chưa nêu ngân sách, giữ nguyên giá trị cũ.");
        // Model có thể bổ sung state để lấy món nhưng không được tự đổi/nới yêu cầu đã chốt.
        // userId luôn lấy từ context đã xác thực, không phải tham số do model cung cấp.
        // Khẩu vị "bữa 2 ngày 1" được giữ đúng bữa trước khi tính vector, kể cả khi model ghi thành cả ngày 1.
        if (!context.ReplanAfterConfirmation)
        {
            var proposedState = AgentConstraintGuard.RetainPlacements(
                AgentMealScopePolicy.Narrow(AgentConstraintGuard.WithoutErased(AgentIntakePolicy.WithCatalogProvince(AgentIntakePolicy.WithoutUnstatedProvince(AgentContextBuilder.KeepKnown(input.State, context.State), context), context), context.State, context.Request.Content), context.State, AgentMealScopePolicy.Evidence(context)), context.PlacedRequests);
            try { context.State = statePolicy.Accept(context.State, proposedState, context.ConstraintBaseline); }
            catch (AgentStateChangeRequiredException change)
            {
                // Nới điều kiện (tăng ngân sách, bỏ yêu cầu món...) do model đọc được áp luôn như ở AcceptRaw của orchestrator:
                // backend không còn giữ chờ xác nhận và không trả câu cố định "Bạn nhắn “ok” để đổi…" (hội thoại 223).
                telemetry.Record("state_change_unstaged", context.ConversationId, context.TurnId, context.AttemptId);
                context.State = change.Proposed with { PendingStateChange = context.State.PendingStateChange, SourceRoadmapUpdatedAt = context.State.SourceRoadmapUpdatedAt,
                    HistoryStartSequence = context.State.HistoryStartSequence, TranscriptStartSequence = context.State.TranscriptStartSequence };
            }
        }
        // STEP-02: When AgentScopedPlanning.IsLongTrip(context.State) holds, context.State.MealSlots.Length is 0 and context.Source is not { Items.Length: > 0 }, set context.State.
        // Backend tự ghép chuyến dài theo bữa, nên phải biết các bữa ngay cả khi model không ghi mealSlots.
        if (AgentScopedPlanning.IsLongTrip(context.State) && context.State.MealSlots.Length == 0 && context.Source is not { Items.Length: > 0 })
            context.State = context.State with { MealSlots = ["breakfast", "lunch", "dinner"] };
        // STEP-03: Prepare state, then call rankedMeals.Clear.
        var state = context.State;
        _rankedMeals.Clear();
        // STEP-04: Return the anonymous result early when AgentReplyBuilder.TripDetailsQuestion() exists or AgentIntakePolicy.PreferencesDue(context) holds.
        // Chỉ ranking khi đã rõ tỉnh, số ngày và ngân sách (hoặc người dùng đã chấp nhận không giới hạn). Thiếu chúng là câu hỏi
        // cho người dùng, không phải lỗi hợp đồng: không throw để lượt chat không thành 503 và mất câu trả lời vừa gửi.
        // Orchestrator hỏi lại bằng câu cố định mà không gọi thêm LLM. Câu hỏi khẩu vị cũng phải đến trước khi ranking, kể cả khi model vừa đọc xong câu trả lời.
        if (AgentReplyBuilder.TripDetailsQuestion(state) is not null)
        {
            context.TripDetailsMissing = true;
            return new { status = "missing_trip_details", rankingMode = "none", requestId = (Guid?)null, items = Array.Empty<object>() };
        }
        // STEP-05: Update context: State.
        // "Phải có món X" chỉ cần X ở một bữa; nếu để cả chuyến, bữa nào cũng bị lọc còn X và bữa sáng không có ứng viên.
        context.State = state = await PlaceNamedRequestsAsync(context, state, ct);
        // STEP-06: Prepare scope as AgentScope.
        var scope = new AgentScope(input.DayNumber, input.MealSlot);
        if (input.MaxPricePerMeal is < AgentStateValidator.MinPlausiblePrice) return PriceUnitError(context, input.MaxPricePerMeal.Value);
        // Tham số sai do model gửi (phạm vi, tag, topK, giá, bán kính...) được trả lại cho model để nó tự sửa trong cùng lượt,
        // thay vì làm hỏng cả lượt chat thành 503/409 như trước. Số vòng tool vẫn bị giới hạn ở orchestrator.
        // STEP-07: Run AgentProposalBuilder.ValidateScope.
        try { AgentProposalBuilder.ValidateScope(context, scope); }
        catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE")
        {
            // STEP-08: On ConflictException when error.ErrorCode is "PROPOSAL_NOT_COMMITTABLE", return the result of ArgumentError.
            return ArgumentError(context, error.Message);
        }
        // STEP-09: Return the result of ArgumentError early when input.Scope is not ("full" or "day" or "slot"), (input.Scope is "full" and more.
        // full = cả chuyến; day = một ngày; slot = một bữa của một ngày.
        // Kiểm tra thêm giá VND, bán kính, số ID và topK để không vượt hợp đồng Recommendation.
        if (input.Scope is not ("full" or "day" or "slot") || input.Scope == "full" && (input.DayNumber.HasValue || input.MealSlot is not null) ||
            input.Scope is "day" or "slot" && !input.DayNumber.HasValue || input.Scope == "day" && input.MealSlot is not null || input.Scope == "slot" && input.MealSlot is null ||
            input.Province is not null && !string.Equals(input.Province.Trim(), state.Province!.Trim(), StringComparison.OrdinalIgnoreCase) ||
            input.TopK < 1 || input.TopK > limits.MaxRanked || input.ExcludeDishIds is null || input.ExcludeDishIds.Length > 2000 || input.ExcludeDishIds.Any(x => x <= 0) ||
            input.TagIds is null || input.ExcludeTagIds is null || input.TagIds.Length > 100 || input.ExcludeTagIds.Length > 100 ||
            input.TagIds.Any(x => x <= 0) || input.ExcludeTagIds.Any(x => x <= 0) ||
            input.MaxPricePerMeal.HasValue && (input.MaxPricePerMeal < 0 || decimal.Truncate(input.MaxPricePerMeal.Value) != input.MaxPricePerMeal || input.MaxPricePerMeal > AgentStateValidator.MaxMoney) ||
            input.RadiusKm.HasValue && (input.RadiusKm < 1 || input.RadiusKm > 50) ||
            input.MaxLegKm.HasValue && (input.MaxLegKm < (decimal)AgentDayClustering.MinLegKm || input.MaxLegKm > (decimal)AgentDayClustering.MaxLegKm))
            return ArgumentError(context, "Phạm vi, topK, giá tối đa, bán kính, maxLegKm (0,5-30) hoặc danh sách ID không đúng hợp đồng get_recommendation.");
        // STEP-10: Load catalogTags through tags.GetTagsAsync.
        var catalogTags = await tags.GetTagsAsync(null, ct);
        // STEP-11: Prepare unknownTags through Concat().Distinct().Except().ToArray.
        var unknownTags = state.TagIds.Concat(state.ExcludeTagIds).Concat(input.TagIds).Concat(input.ExcludeTagIds)
            .Concat(state.MealRequests.SelectMany(request => request.TagIds)).Distinct().Except(catalogTags.Select(tag => tag.Id)).ToArray();
        // STEP-12: Return the result of ArgumentError early when unknownTags.Length > 0.
        if (unknownTags.Length > 0)
            return ArgumentError(context, $"Tag {string.Join(", ", unknownTags)} không có trong catalog. Chỉ dùng ID có trong catalogTags.");
        // STEP-13: Prepare alternative.
        var alternative = context.Request.Command == "alternative" || input.Alternative;
        // STEP-14: When alternative is true and context.AlternativeScopes does not contain scope, call context.AlternativeScopes.Add.
        if (alternative && !context.AlternativeScopes.Contains(scope)) context.AlternativeScopes.Add(scope);
        // STEP-15: Prepare parts through HomogeneousScopes.
        // Ngày/bữa có khẩu vị hoặc yêu cầu món khác nhau được backend tự tách thành các phạm vi đồng nhất ngay trong lượt gọi này.
        // Model chỉ cần một lần gọi cho cả phạm vi; mỗi phần có vector, bộ lọc và grant riêng nên không lẫn ứng viên giữa các bữa.
        var parts = HomogeneousScopes(state, scope);
        // STEP-16: When parts.Length > 1, call telemetry.Record.
        if (parts.Length > 1)
            telemetry.Record("scope_split", context.ConversationId, context.TurnId, context.AttemptId, new { parts = parts.Length });
        // STEP-17: Prepare usedNames as HashSet<string> and rankings as List<ScopedRanking>.
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var rankings = new List<ScopedRanking>();
        // STEP-18: For each part in parts, prepare ranking through RankScopeAsync, return notice early when ranking.Notice exists and 1 more action(s).
        foreach (var part in parts)
        {
            var ranking = await RankScopeAsync(context, input, scope, part, catalogTags, alternative, usedNames, ct);
            if (ranking.Notice is { } notice) return notice;
            rankings.Add(ranking);
        }
        // STEP-19: Return the result of Notice early when rankings.All matches the filter.
        // Không có món phù hợp ở mọi bữa: trả danh sách rỗng/cảnh báo để model hỏi lại, không tự nới điều kiện.
        if (rankings.All(x => x.RequestIds.Count == 0))
            return Notice(context, rankings.Any(x => x.Coverage.Length > 0) ? AgentReplyBuilder.Coverage(context)
                : "Chưa tìm thấy món khả dụng đáp ứng các điều kiện hiện tại. Bạn có muốn điều chỉnh điều kiện hoặc chọn bữa khác không?", "no_candidates");
        // STEP-19b: Keep each day's meals close together, picking deeper in the same reco lists when needed.
        var (suggestedDays, legWarnings, suggestedTotal) = ArrangeDays(context, state, scope, input, rankings);
        // STEP-20: Prepare coverage through rankings.SelectMany(filtered).ToArray and returned through DistinctBy(filtered).Count.
        var coverage = rankings.SelectMany(x => x.Coverage).ToArray();
        var returned = rankings.SelectMany(x => x.Picked).DistinctBy(x => x.Id).Count();
        // STEP-21: Update context: ToolNotice.
        context.ToolNotice = returned == 0
            ? (coverage.Length > 0 ? AgentReplyBuilder.Coverage(context)
                : "Chưa có kết quả gợi ý khả dụng cho phạm vi này. Bạn có muốn điều chỉnh điều kiện không?")
            : null;
        // STEP-22: Prepare personalized through rankings.SelectMany(filtered).All, coverageWarning from rankings.Any, warnings from Array.Empty and more.
        var personalized = rankings.SelectMany(x => x.RequestIds).All(id => id.HasValue);
        var coverageWarning = coverage.Any(meal => meal.AvailableCount == 0)
            ? "Có bữa chưa có ứng viên phù hợp trong kết quả này. Kiểm tra mealCoverage và lấy gợi ý riêng cho bữa đó trước khi kết luận thiếu món."
            : rankings.Any(x => !x.PerMeal && x.ExpectedMeals > x.Picked.Count) ? "Có thể chưa đủ lựa chọn cho các bữa; cần lấy thêm gợi ý hoặc hỏi người dùng, không tự nới điều kiện." : null;
        string[] warnings = [.. personalized ? Array.Empty<string>() : ["Đang dùng thứ tự đánh giá thay cho cá nhân hóa."], .. legWarnings];
        var candidateCount = rankings.Sum(x => x.CandidateCount);
        var ignoredCount = rankings.Sum(x => x.IgnoredCount);
        var mealCoverage = coverage.Select(x => new { x.DayNumber, x.MealSlot, x.AvailableCount, x.RequiredTags });
        // Ngân sách chia đều cho các bữa: mốc giá để model chọn món vừa túi tiền, không phải bộ lọc (một bữa có thể đắt hơn nếu bữa khác rẻ hơn).
        var budgetPerMeal = AgentScopedPlanning.BudgetPerMeal(state);
        // suggestedDays already prefers dishes within budgetPerMeal; budgetFits=false means even those picks cost more than the budget, so the model asks about it.
        bool? budgetFits = state.BudgetAmount is { } budget && suggestedTotal is { } suggestedSum ? suggestedSum <= budget : null;
        // STEP-23: Return the anonymous result early when rankings.Count is 1.
        // Payload gọn cho LLM: tọa độ do backend tự tra khi tính tuyến, bữa ăn nằm ở allowedMealSlots nên tags chỉ giữ mã loại món.
        // Giữ thông báo fallback để model không mô tả thứ tự theo đánh giá là ranking cá nhân hóa.
        if (rankings.Count == 1)
            return new
            {
                rankingMode = personalized ? "personalized" : "rating_fallback",
                selectionScope = new { scope.DayNumber, scope.MealSlot },
                candidateCount, ignoredCount, returnedCount = returned, mealCoverage, coverageWarning, warnings, budgetPerMeal, suggestedTotal, budgetFits,
                suggestedDays, items = Items(rankings[0])
            };
        // STEP-24: Return the anonymous result.
        // Phạm vi đã tách: mỗi scopedResults có selectionScope riêng, món chỉ được dùng đúng ngày/bữa của phần đó.
        return new
        {
            rankingMode = personalized ? "personalized" : "rating_fallback",
            selectionScope = new { scope.DayNumber, scope.MealSlot },
            candidateCount, ignoredCount, returnedCount = returned, mealCoverage, coverageWarning, warnings, budgetPerMeal, suggestedTotal, budgetFits,
            suggestedDays, scopedResults = rankings.Select(x => new { selectionScope = new { x.Scope.DayNumber, x.Scope.MealSlot }, items = Items(x) })
        };
    }

    /// <summary>
    /// Keeps the meals of each day in scope close together (<see cref="AgentDayClustering"/>). A day or trip scope returns one dish per
    /// meal per day for the model to take; an edit of one meal puts the dishes near the day's other meals first. Dishes picked from deeper
    /// in a reco list get the grant of that same list, so nothing outside the ranked, filtered candidates is ever offered.
    /// </summary>
    private static (object[] Days, string[] Warnings, decimal? Total) ArrangeDays(AgentContext context, AgentState state, AgentScope scope,
        AgentRecommendationInput input, List<ScopedRanking> rankings)
    {
        var legKm = input.MaxLegKm is { } stated ? Math.Clamp((double)stated, AgentDayClustering.MinLegKm, AgentDayClustering.MaxLegKm) : AgentDayClustering.DefaultLegKm;
        static (double Latitude, double Longitude)? Position(ANGI.Domain.Entities.Dish dish) =>
            dish.Restaurant is { } restaurant ? ((double)restaurant.Latitude, (double)restaurant.Longitude) : null;
        // Một bữa: đưa món gần các bữa còn lại của ngày lên đầu, rồi chỉ gửi model đúng số món nó xin.
        if (scope.DayNumber is { } slotDay && scope.MealSlot is { } slotMeal)
        {
            var neighbours = Neighbours(context, scope);
            foreach (var ranking in rankings)
            {
                if (neighbours.Length == 0 || ranking.Pools?.GetValueOrDefault(slotMeal) is not { Length: > 0 } pool) continue;
                var keep = AgentDayClustering.NearNeighbours(pool, Position, neighbours, legKm).Take(Math.Max(1, input.TopK)).ToArray();
                foreach (var dish in keep) Promote(context, ranking.Scope.DayNumber, slotMeal, dish.Id);
                ranking.Picked.Clear();
                ranking.Picked.AddRange(keep);
                ranking.ChosenByMeal[slotMeal] = keep;
            }
            return ([], [], null);
        }
        var meals = AgentStateValidator.Meals.Where(state.MealSlots.Contains).ToArray();
        if (meals.Length < 2) return ([], [], null);
        var days = AgentScopedPlanning.Slots(state, scope).Select(slot => slot.Day).Distinct().Order().ToArray();
        var dishes = new Dictionary<int, ANGI.Domain.Entities.Dish>();
        var options = new Dictionary<(short, string), IReadOnlyList<AgentDayClustering.Option>>();
        var budgetPerMeal = AgentScopedPlanning.BudgetPerMeal(state);
        ScopedRanking? Owner(short day, string meal) => rankings.FirstOrDefault(ranking => ranking.Scope.Covers(new AgentScope(day, meal)) && ranking.Pools?.ContainsKey(meal) == true);
        IReadOnlyList<AgentDayClustering.Option> Candidates(short day, string meal)
        {
            if (options.TryGetValue((day, meal), out var known)) return known;
            var list = Affordable((Owner(day, meal)?.Pools![meal] ?? []).Where(dish => Position(dish) is not null), budgetPerMeal).Select(dish =>
            {
                dishes[dish.Id] = dish;
                var (latitude, longitude) = Position(dish)!.Value;
                return new AgentDayClustering.Option(dish.Id, AgentScopedPlanning.Normalize(dish.Name)!, latitude, longitude);
            }).ToArray();
            return options[(day, meal)] = list;
        }
        // Món giữ nguyên ở các ngày ngoài phạm vi không được lặp lại ở ngày đang xếp.
        var used = (context.Source?.Items ?? []).Where(item => !scope.Contains(item)).Select(item => AgentScopedPlanning.Normalize(item.DishName))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var plans = AgentDayClustering.Arrange(days, meals, Candidates, legKm, used, day => StartPoint(state, day));
        // The even per-meal share alone can leave the total a little over a budget the trip can meet; cheaper dishes of the same meal close the gap.
        var keptOutside = (context.Source?.Items ?? []).Where(item => !scope.Contains(item)).Sum(item => item.Price);
        if (state.BudgetAmount is { } tripBudget)
            plans = AgentDayClustering.FitBudget(plans, Candidates, id => dishes[id].Price, tripBudget - keptOutside, legKm);
        foreach (var plan in plans)
            foreach (var pick in plan.Meals)
            {
                var owner = Owner(plan.DayNumber, pick.MealSlot)!;
                var dish = dishes[pick.Dish.DishId];
                var chosen = owner.ChosenByMeal.GetValueOrDefault(pick.MealSlot) ?? [];
                if (chosen.All(other => other.Id != dish.Id)) owner.ChosenByMeal[pick.MealSlot] = [dish, .. chosen];
                if (owner.Picked.All(other => other.Id != dish.Id)) owner.Picked.Add(dish);
                Promote(context, owner.Scope.DayNumber, pick.MealSlot, dish.Id);
            }
        var warnings = input.MaxLegKm is null ? [] : plans.Where(plan => plan.LegsKm.Any(leg => leg > legKm))
            .Select(plan => $"Ngày {plan.DayNumber}: có chặng {plan.LegsKm.Max()} km, xa hơn maxLegKm vì không còn món phù hợp gần hơn.").ToArray();
        object[] suggested = plans.Select(plan => (object)new
        {
            plan.DayNumber, meals = plan.Meals.Select(pick => new { pick.MealSlot, pick.Dish.DishId, dishes[pick.Dish.DishId].Name }), plan.LegsKm, plan.StartKm
        }).ToArray();
        // The plan's total with these picks: the kept dishes outside this scope plus the suggested ones.
        var kept = (context.Source?.Items ?? []).Where(item => !scope.Contains(item)).Sum(item => item.Price);
        var total = plans.Length == 0 ? (decimal?)null : kept + plans.SelectMany(plan => plan.Meals).Sum(pick => dishes[pick.Dish.DishId].Price);
        return (suggested, warnings, total);
    }

    /// <summary>
    /// One meal's candidates with the budget in mind: dishes within the even per-meal share come first in rank order, then the others from cheapest up.
    /// None is dropped: a hard cap left so few dishes that one was repeated four times in a plan (conversation 471). Without a budget the rank order
    /// is unchanged. Reads prices only, never wording.
    /// </summary>
    private static IEnumerable<ANGI.Domain.Entities.Dish> Affordable(IEnumerable<ANGI.Domain.Entities.Dish> ranked, decimal? perMeal)
    {
        if (perMeal is not { } cap) return ranked;
        var all = ranked.ToArray();
        return [.. all.Where(dish => dish.Price <= cap), .. all.Where(dish => dish.Price > cap).OrderBy(dish => dish.Price)];
    }

    /// <summary>True when a dish of the plan on that day sits outside the day's place, beyond any radius a meal was widened to.</summary>
    private static bool OutsideArea(AgentContext context, short day)
    {
        if (context.State.DayAreas?.FirstOrDefault(area => area.DayNumber == day) is not { } area) return false;
        return (context.Source?.Items ?? []).Any(item => item.DayNumber == day && item.Latitude is { } lat && item.Longitude is { } lng &&
            AgentDayClustering.Km((double)area.Latitude, (double)area.Longitude, (double)lat, (double)lng) >
                (double)Math.Max(area.RadiusKm, context.State.WidenedMeals?.FirstOrDefault(meal => meal.DayNumber == day && meal.MealSlot == item.MealSlot)?.RadiusKm ?? 0) + 1);
    }

    /// <summary>
    /// Where a day starts: the place the user named for that day, otherwise their current location or the point they pinned on the map;
    /// null when the trip searches the whole province.
    /// </summary>
    private static (double Latitude, double Longitude)? StartPoint(AgentState state, short day) =>
        state.DayAreas?.FirstOrDefault(area => area.DayNumber == day) is { } area ? ((double)area.Latitude, (double)area.Longitude)
        : state.OriginMode is "gps" or "pinned" && state.OriginLatitude is { } latitude && state.OriginLongitude is { } longitude ? ((double)latitude, (double)longitude)
        : null;

    /// <summary>
    /// What a one-meal edit should stay near: the other meals kept on that day, or the day's starting point when the meal is alone on it.
    /// </summary>
    private static (double Latitude, double Longitude)[] Neighbours(AgentContext context, AgentScope scope)
    {
        if (scope is not { DayNumber: { } day, MealSlot: { } meal }) return [];
        var kept = (context.Source?.Items ?? []).Where(item => item.DayNumber == day && item.MealSlot != meal && item.Latitude.HasValue && item.Longitude.HasValue)
            .Select(item => ((double)item.Latitude!.Value, (double)item.Longitude!.Value)).ToArray();
        return kept.Length > 0 ? kept : StartPoint(context.State, day) is { } start ? [start] : [];
    }

    /// <summary>Grants a dish taken from the reserve of the same reco list, day and meal; a dish already granted is left as it is.</summary>
    private static void Promote(AgentContext context, short? day, string meal, int dishId)
    {
        if (context.Grants.Any(grant => grant.DayNumber == day && grant.MealSlot == meal && grant.DishIds.Contains(dishId))) return;
        if (context.ReserveGrants.LastOrDefault(grant => grant.DayNumber == day && grant.MealSlot == meal && grant.DishIds.Contains(dishId)) is { } reserve)
            context.Grants.Add(reserve with { DishIds = [dishId] });
    }

    /// <summary>Moves dish/restaurant requests wider than a meal onto one meal that can serve them; an unresolved name keeps its normal catalog question.</summary>
    private async Task<AgentState> PlaceNamedRequestsAsync(AgentContext context, AgentState state, CancellationToken ct)
    {
        // STEP-01: Return state early when catalogSearch is missing.
        if (catalogSearch is null) return state;
        // STEP-02: Prepare messages through Select(filtered).Append().ToArray.
        var messages = context.History.Where(turn => turn.Role == "user").Select(turn => turn.Content).Append(context.Request.Content).ToArray();
        // STEP-03: Go through each request in AgentConstraintGuard.PlaceableRequests().
        foreach (var request in AgentConstraintGuard.PlaceableRequests(state, messages))
        {
            // STEP-04: Call FittingMealsAsync and keep fitting.
            var fitting = await FittingMealsAsync(context, state, request, ct);
            // STEP-05: Skip this round when fitting is missing or AgentConstraintGuard.PlaceSlot() is not { } slot.
            if (fitting is null || AgentConstraintGuard.PlaceSlot(state, request, fitting) is not { } slot) continue;
            // STEP-06: Set state.
            state = AgentConstraintGuard.PlaceRequest(state, request, slot.Day, slot.Meal);
            // STEP-07: Queue state.MealRequests[^1] through context.PlacedRequests.Add.
            context.PlacedRequests.Add(state.MealRequests[^1]);
            // STEP-08: When context.ConstraintBaseline exists, set context.ConstraintBaseline.
            // Yêu cầu rộng đã có từ lượt trước không tính là nới ràng buộc khi được chuyển về một bữa.
            if (context.ConstraintBaseline is { } baseline)
                context.ConstraintBaseline = AgentConstraintGuard.RebasePlacement(baseline, request, slot.Day, slot.Meal);
        }
        // A dish or restaurant put on a meal stays on that meal even when its catalog tags list another one: placement above already picks
        // a fitting meal for a trip-wide name, and a meal the user or model named is kept (user decision 2026-11-11; Round 3 bug report: "3 món này
        // vào bữa sáng 3 ngày" was moved to lunch and dinner because a wording reader missed "cả 3 ngày").
        // STEP-09: Return state.
        return state;
    }

    /// <summary>Meals the named dish or restaurant can serve, best first; null when the name is unresolved so the ranking step reports it.</summary>
    private async Task<string[]?> FittingMealsAsync(AgentContext context, AgentState state, AgentMealRequest request, CancellationToken ct)
    {
        // STEP-01: Prepare meals through AgentConstraintGuard.PlaceableMeals and restaurantId.
        var meals = AgentConstraintGuard.PlaceableMeals(state);
        long? restaurantId = null;
        // STEP-02: When request.RestaurantName exists, prepare restaurant through LookupAsync, return null early when restaurant.Status is not "found" and 1 more action(s).
        if (request.RestaurantName is { } restaurantName)
        {
            var restaurant = await LookupAsync(context, new("restaurant", restaurantName, state.Province, request.RestaurantId), ct);
            if (restaurant.Status != "found") return null;
            restaurantId = restaurant.Matches.Single().RestaurantId;
        }
        // STEP-03: Prepare dishIds.
        int[] dishIds = [];
        // STEP-04: When request.DishName exists, run the branch below.
        if (request.DishName is { } dishName)
        {
            // STEP-05: Call LookupAsync and keep lookup.
            var lookup = await LookupAsync(context, new("dish", dishName, state.Province), ct);
            // STEP-06: Return null early when lookup.Status is not "found" or "family".
            if (lookup.Status is not ("found" or "family")) return null;
            // STEP-07: Set dishIds.
            dishIds = lookup.Matches.Where(match => match.Available && (!restaurantId.HasValue || match.RestaurantId == restaurantId))
                .Select(match => checked((int)match.Id)).ToArray();
            // STEP-08: Return null early when dishIds.Length is 0.
            if (dishIds.Length == 0) return null;
        }
        // STEP-09: Prepare fitting as List<string>.
        var fitting = new List<string>();
        // STEP-10: For each meal in meals, prepare filter as DishCandidateFilter, prepare ids through dishes.GetCandidateIdsAsync and 1 more action(s).
        foreach (var meal in meals)
        {
            var filter = new DishCandidateFilter { Province = state.Province, RestaurantId = restaurantId, DishIds = dishIds, RequireAllTags = true, CompatibleMealTimeCode = meal };
            var ids = await dishes.GetCandidateIdsAsync(filter, 1, ct);
            if (ids is { Count: > 0 }) fitting.Add(meal);
        }
        // STEP-11: Return one of two results depending on fitting.Count > 0.
        // Không bữa nào hợp: vẫn xếp vào bữa ưu tiên để bước xếp hạng báo thiếu đúng bữa đó.
        return fitting.Count > 0 ? [.. fitting] : meals;
    }

    /// <summary>Splits a broad scope into day scopes, or meal scopes of a day, whose positions share one taste and meal request.</summary>
    private static AgentScope[] HomogeneousScopes(AgentState state, AgentScope scope)
    {
        // STEP-01: Return the collected items early when IsHomogeneous(state, scope) holds.
        if (IsHomogeneous(state, scope)) return [scope];
        // STEP-02: Prepare days from Enumerable.Range and meals from AgentStateValidator.Meals.Where.
        var days = scope.DayNumber is { } only ? [only] : Enumerable.Range(1, state.NumDays ?? 0).Select(day => (short)day).ToArray();
        var meals = state.MealSlots.Length > 0 ? AgentStateValidator.Meals.Where(state.MealSlots.Contains).ToArray() : AgentStateValidator.Meals;
        // STEP-03: Return the result of days.SelectMany(filtered).ToArray.
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
        // Căn cứ để model viết giới thiệu món; cắt ngắn vì trường này được gửi lại ở mọi vòng gọi tiếp theo trong lượt.
        description = x.Description is { Length: > 160 } ? x.Description[..160] : x.Description,
        allowedMealSlots = ranking.PerMeal
            ? AgentStateValidator.Meals.Where(slot => ranking.ChosenByMeal.GetValueOrDefault(slot)?.Any(d => d.Id == x.Id) == true).ToArray()
            // A one-meal search already passed that meal's filter (or holds the dish the user named for it), so it is allowed there.
            : ranking.Scope.MealSlot is { } only ? [only] : AgentStateValidator.Meals.Where(slot => FitsMeal(x, slot)).ToArray(),
        tags = x.DishTags.Where(t => t.Tag.TagType != ANGI.Domain.Enums.TagType.MealTime).Select(t => t.Tag.Code).ToArray()
    });

    /// <summary>Ranks one homogeneous scope and issues its grants; exclusions follow the scope the model requested.</summary>
    private async Task<ScopedRanking> RankScopeAsync(AgentContext context, AgentRecommendationInput input, AgentScope requested,
        AgentScope scope, IReadOnlyCollection<ANGI.Domain.Entities.Tag> catalogTags, bool alternative, HashSet<string> usedNames, CancellationToken ct)
    {
        // STEP-01: Prepare state and slots through AgentScopedPlanning.Slots().ToArray.
        var state = context.State;
        var slots = AgentScopedPlanning.Slots(state, scope).ToArray();
        // STEP-02: Set (day, meal).
        var (day, meal) = slots[0];
        // STEP-03: Prepare mealRequest through AgentScopedPlanning.Request, tagIds through Concat().Concat().Distinct().ToArray and more.
        var mealRequest = AgentScopedPlanning.Request(state, day, meal);
        // Tag phải có thật trong catalog. Gộp các bộ lọc theo hướng giữ/siết ràng buộc state.
        var tagIds = state.TagIds.Concat(input.TagIds!).Concat(mealRequest.TagIds).Distinct().ToArray();
        var excludeTags = state.ExcludeTagIds.Concat(input.ExcludeTagIds!).Distinct().ToArray();
        // STEP-04: Throw the error from Invalid when tagIds.Concat().Except() has items.
        if (tagIds.Concat(excludeTags).Except(catalogTags.Select(x => x.Id)).Any()) throw Invalid("Bộ lọc chứa tag không có trong catalog.");
        // STEP-05: Prepare excludedItems through Items ?? [].Where(filtered).ToArray, excluded through Concat().Distinct().ToArray and more.
        // Khi thay món, tránh chọn lại món đang giữ ở ngoài scope model yêu cầu.
        // Với command alternative, loại cả món cũ trong scope để phương án mới khác bản trước.
        var excludedItems = (context.Source?.Items ?? []).Where(item => !requested.Contains(item) || alternative || mealRequest.DifferentFromPlan).ToArray();
        var excluded = excludedItems.Select(x => x.DishId).Concat(input.ExcludeDishIds!).Distinct().ToArray();
        // Cùng món ở quán khác vẫn là món cũ: loại theo tên để "món khác" không trả lại đúng món vừa đổi đi.
        var excludedNames = excludedItems.Select(x => AgentScopedPlanning.Normalize(x.DishName)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        long? restaurantId = null;
        int[] namedDishIds = [];
        // STEP-06: When mealRequest.RestaurantName exists, prepare lookup through LookupAsync and more.
        if (mealRequest.RestaurantName is { } restaurantName)
        {
            var lookup = await LookupAsync(context, new("restaurant", restaurantName, state.Province, mealRequest.RestaurantId), ct);
            if (lookup.Status != "found") return ScopedRanking.Stopped(scope, CatalogNotice(context, Describe(lookup), lookup.Status));
            restaurantId = lookup.Matches.Single().RestaurantId;
        }
        // STEP-07: When mealRequest.DishName exists, prepare lookup through LookupAsync and more.
        if (mealRequest.DishName is { } dishName)
        {
            var lookup = await LookupAsync(context, new("dish", dishName, state.Province), ct);
            if (lookup.Status is not ("found" or "family")) return ScopedRanking.Stopped(scope, CatalogNotice(context, Describe(lookup), lookup.Status));
            namedDishIds = lookup.Matches.Where(x => x.Available && (!restaurantId.HasValue || x.RestaurantId == restaurantId))
                .Select(x => checked((int)x.Id)).ToArray();
            if (namedDishIds.Length == 0)
                return ScopedRanking.Stopped(scope, CatalogNotice(context, "Chưa tìm thấy món được yêu cầu trong menu khả dụng của nhà hàng đã chọn. Bạn có muốn chọn món hoặc nhà hàng khác không?", "menu_mismatch"));
        }
        // STEP-08: Prepare maxPrice through Min, place from FirstOrDefault, radius through Min, area and 12 more.
        // Hai giới hạn cùng tồn tại thì lấy giá trị nhỏ hơn: tool không được tăng trần giá/bán kính của state.
        var maxPrice = Min(state.MaxPricePerMeal, input.MaxPricePerMeal);
        // Ngày có nơi đến trong hành trình thì tìm quanh nơi đó, thay cho điểm xuất phát chung của chuyến.
        var place = state.DayAreas?.FirstOrDefault(x => x.DayNumber == day);
        // radiusKm của model chỉ siết thêm khi có tâm để đo (nơi đến của ngày hoặc điểm xuất phát); không có tâm thì bỏ qua thay vì làm hỏng lượt gọi.
        var radius = Min(place?.RadiusKm ?? state.RadiusKm, place is not null || state.OriginLatitude is not null ? input.RadiusKm : null);
        var area = radius is { } km && (place?.Latitude ?? state.OriginLatitude) is { } lat && (place?.Longitude ?? state.OriginLongitude) is { } lng ? new NearbyArea(lat, lng, km) : null;
        // Người dùng nêu tên nơi đến: giữa các món hợp khẩu vị gần bằng nhau thì ưu tiên quán gần hơn. Toàn tỉnh, GPS và điểm chấm trên bản đồ không đi nhánh này.
        var namedCenter = AgentScopedPlanning.NamedArea(state, day);
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
        // Toàn bộ danh sách reco đã lọc của từng bữa (cả phần dự phòng): bước giữ các bữa gần nhau chọn sâu hơn suất gửi model.
        var pools = new Dictionary<string, ANGI.Domain.Entities.Dish[]>();
        var picked = new List<ANGI.Domain.Entities.Dish>();
        var requestIds = new List<Guid?>();
        int candidateTotal = 0, ignoredTotal = 0;
        // STEP-09: Go through each mealSlot in meals.
        foreach (var mealSlot in meals)
        {
            // STEP-10: Prepare mealTags from tagIds.Append.
            // Bữa sáng/trưa/tối mặc định chỉ xếp hạng món chính, trừ khi người dùng đã chọn loại món/món/quán cho bữa đó
            // (ví dụ "bữa sáng chỉ đồ uống") hoặc model đang tìm món phụ bằng tagIds loại món.
            var mealTags = mainTag is not null && mealSlot is not null && !tagIds.Contains(mainTag.Id) &&
                AgentScopedPlanning.RequiresMain(state, slots.First(x => x.Meal == mealSlot).Day, mealSlot, catalogTags, input.TagIds)
                ? tagIds.Append(mainTag.Id).ToArray() : tagIds;
            async Task<ANGI.Domain.Entities.Dish[]> EligibleAsync(NearbyArea? searchArea)
            {
                var filter = new DishCandidateFilter { Province = state.Province, TagIds = mealTags, ExcludeTagIds = excludeTags,
                    ExcludeDishIds = excluded, MaxPrice = maxPrice, Area = searchArea, RestaurantId = restaurantId, DishIds = namedDishIds,
                    // A dish the user named goes on the meal they named, whatever meal its catalog tags list; picks of our own keep the tags.
                    RequireAllTags = true, CompatibleMealTimeCode = namedDishIds.Length > 0 ? null : mealSlot };
                // Backend lọc catalog TRƯỚC, chỉ gửi tối đa MaxCandidates IDs hợp lệ sang Recommendation.
                // Recommendation xếp hạng tập được gửi; không tự tìm món từ schema core hoặc gọi ngược backend.
                // Catalog có rất nhiều bản cùng tên ở nhiều quán: lấy rộng rồi gom theo tên, giữ quán đứng đầu thứ tự rating,
                // để reco xếp hạng các món khác nhau thay vì hàng trăm bản sao của vài món.
                var pool = await dishes.GetCandidateIdsAsync(filter, Math.Max(limits.MaxCandidates, CandidatePool), ct);
                var cards = pool.Count == 0 ? new Dictionary<int, ANGI.Domain.Entities.Dish>()
                    : (await dishes.GetCardDishesAsync(pool.ToArray(), ct)).ToDictionary(dish => dish.Id);
                // Discovery gộp tag cùng loại bằng OR; ràng buộc Agent cần món thỏa TẤT CẢ tag được yêu cầu.
                var qualified = pool.Where(cards.ContainsKey).Select(id => cards[id])
                    .Where(dish => mealTags.All(id => dish.DishTags.Any(tag => tag.TagId == id)) && (namedDishIds.Length > 0 || FitsMeal(dish, mealSlot)) &&
                        (namedDishIds.Length > 0 || !excludedNames.Contains(AgentScopedPlanning.Normalize(dish.Name)!)));
                if (distinctNames) qualified = namedCenter is not null
                    ? NearestBranches(qualified, dish => AgentScopedPlanning.Normalize(dish.Name), namedCenter)
                    : qualified.DistinctBy(dish => AgentScopedPlanning.Normalize(dish.Name));
                // Món người dùng đã đổi đi trong chuyến này không quay lại khi họ đổi tiếp ("món khác" nhiều lần), trừ khi không còn món nào khác.
                var replaced = (state.ReplacedDishNames ?? []).Select(AgentScopedPlanning.Normalize).ToHashSet();
                var found = namedDishIds.Length > 0 ? qualified.ToArray() : qualified.Where(dish => !replaced.Contains(AgentScopedPlanning.Normalize(dish.Name))).ToArray();
                return found.Length > 0 ? found : qualified.ToArray();
            }
            // STEP-11: Prepare vectorSlot through slots.First, mealKey from AgentScopedPlanning.Fingerprint, known from rankedMeals.GetValueOrDefault and eligible from EligibleAsync.
            var vectorSlot = slots.First(x => mealSlot is null || x.Meal == mealSlot);
            var mealKey = AgentScopedPlanning.IsLongTrip(state) && perMealSearch ? $"{mealSlot}|{AgentScopedPlanning.Fingerprint(state, vectorSlot.Day, vectorSlot.Meal)}" : null;
            var known = mealKey is not null ? _rankedMeals.GetValueOrDefault(mealKey) : null;
            var eligible = known?.Eligible ?? await EligibleAsync(area);
            // STEP-12: When namedCenter exists, mealSlot exists, distinctNames is true and 1 more condition(s), prepare widenedKm, check whether eligible.Length is 0 and 2 more action(s).
            // Nơi đến người dùng nêu tên mà bữa này không có món trong bán kính: chỉ bữa này được tìm xa dần, các bữa khác của ngày vẫn giữ bán kính cũ.
            // Món hoặc quán được yêu cầu đích danh giữ cách báo thiếu cũ, vì tìm xa hơn không tạo ra đúng món/quán đó.
            if (namedCenter is not null && mealSlot is not null && distinctNames && restaurantId is null)
            {
                decimal? widenedKm = null;
                if (eligible.Length == 0)
                    foreach (var step in AgentPlaceResolver.RadiusSteps.Where(step => step > namedCenter.RadiusKm))
                    {
                        eligible = await EligibleAsync(namedCenter with { RadiusKm = step });
                        if (eligible.Length == 0) continue;
                        widenedKm = step;
                        break;
                    }
                context.State = state = AgentScopedPlanning.WithWidenedMeal(state, slots.Where(x => x.Meal == mealSlot).Select(x => x.Day), mealSlot, widenedKm);
                if (widenedKm is { } reached)
                    telemetry.Record("meal_area_widened", context.ConversationId, context.TurnId, context.AttemptId, new { day, mealSlot, reached });
            }
            // STEP-13: Prepare candidates through eligible.Select(filtered).Take().ToArray.
            IReadOnlyList<int> candidates = eligible.Select(dish => dish.Id).Take(limits.MaxCandidates).ToArray();
            // STEP-14: Set candidateTotal.
            candidateTotal += candidates.Count;
            // STEP-15: Skip this round when candidates.Count is 0.
            // Bữa không có món phù hợp được ghi nhận qua mealCoverage = 0; không tự nới điều kiện.
            if (candidates.Count == 0)
            {
                if (mealSlot is not null) chosenByMeal[mealSlot] = [];
                continue;
            }
            // STEP-16: When requestIds.Count is 0, set context.State.
            if (requestIds.Count == 0)
                context.State = state = await (preferences ?? new AgentPreferenceService(recommendation)).EnsureAsync(context.State, context.UserId, ct);
            // STEP-17: Prepare topK from Math.Max.
            // Xin dư để còn đủ món sau khi bỏ trùng tên; reco chỉ tính lượt hiển thị qua /impressions khi lịch được lưu.
            // Khi đa dạng hóa, xin thêm đúng số tên đã dùng: sau khi đẩy các tên đó xuống cuối vẫn còn đủ món mới cho suất của bữa.
            // Phần vượt suất không gửi cho model (grant dự phòng), chỉ để backend có món rẻ hơn hoặc khác tên khi cần thay, nên không tốn thêm token LLM.
            var topK = perMealSearch ? Math.Min(limits.MaxRanked, Math.Max(mealQuota * 3, ReserveDepth)) : input.TopK;
            // Đổi riêng một bữa khi ngày đó còn bữa khác: xếp hạng sâu hơn để còn món gần các bữa đó; model vẫn chỉ nhận topK món.
            if (!perMealSearch && mealSlot is not null && requested == scope && Neighbours(context, scope).Length > 0) topK = Math.Min(limits.MaxRanked, Math.Max(input.TopK, NeighbourDepth));
            // STEP-18: When diversify is true, set topK.
            if (diversify) topK = Math.Min(limits.MaxRanked, topK + usedNames.Count);
            // STEP-19: When namedCenter exists and perMealSearch is true, set topK.
            // Xét độ gần cần thấy đủ các món gần bằng điểm nhau, kể cả món ngoài suất. Phần này không gửi cho model nên không tăng token LLM.
            if (namedCenter is not null && perMealSearch) topK = limits.MaxRanked;
            // STEP-20: When mealKey exists, set topK.
            // Chuyến dài cần nhiều món khác tên nhất có thể để ít phải lặp; phần ngoài suất cũng không gửi cho model.
            if (mealKey is not null) topK = limits.MaxRanked;
            // STEP-21: Prepare ranked from candidates.Take.
            // ĐÂY LÀ LỜI GỌI RECOMMENDATION (RECO-05), khác với lời gọi LLM ở AgentOrchestrator.
            // IRecommendationService dùng cấu hình/kết nối riêng; API key LLM không truyền qua tool này.
            var ranked = known?.Ranked ?? (state.Preferences?.Initialized == true
                ? await recommendation.RecommendConversationAsync(context.UserId, candidates, topK,
                    new RecommendationVector("conversation", AgentScopedPlanning.Vector(state, vectorSlot.Day, vectorSlot.Meal)), ct)
                : new RecommendationResult(null, candidates.Take(topK).ToArray(), []));
            // STEP-22: When ranked.RequestId has a value and ranked.VectorSource is not "conversation", set ranked.
            if (ranked.RequestId.HasValue && ranked.VectorSource != "conversation")
                ranked = new RecommendationResult(null, candidates.Take(topK).ToArray(), []);
            // STEP-23: Prepare allowed through candidates.ToHashSet.
            var allowed = candidates.ToHashSet();
            // STEP-24: Throw the error from Invalid when ranked.DishIds.Count > topK, ranked.DishIds.Any matches the filter, ranked.IgnoredDishIds.Any matches the filter or 3 more condition(s).
            // Không tin ID/score chỉ vì request thành công: IDs phải thuộc input, số lượng không vượt topK,
            // score phải hữu hạn và khớp số món. Personalized có requestId; rating fallback không có score/requestId.
            if (ranked.DishIds.Count > topK || ranked.DishIds.Any(id => !allowed.Contains(id)) ||
                ranked.IgnoredDishIds.Any(id => !allowed.Contains(id)) || ranked.RequestId == Guid.Empty ||
                ranked.RequestId.HasValue && (ranked.Scores is null || ranked.Scores.Count != ranked.DishIds.Count || ranked.Scores.Any(x => !double.IsFinite(x))) ||
                !ranked.RequestId.HasValue && ranked.Scores is not null)
                throw Invalid("Dịch vụ gợi ý trả kết quả không hợp lệ.");
            // STEP-25: Add ranked.RequestId to requestIds.
            requestIds.Add(ranked.RequestId);
            // STEP-26: Set ignoredTotal.
            ignoredTotal += ranked.IgnoredDishIds.Count;
            // STEP-27: Prepare rankedIds through ranked.DishIds.Distinct().ToArray and catalog from dishes.GetCardDishesAsync.
            // Loại ID trùng nhưng giữ thứ tự ranking; lấy metadata từ catalog backend thay vì để LLM tự bịa.
            var rankedIds = ranked.DishIds.Distinct().ToArray();
            var catalog = known?.Catalog ?? (await dishes.GetCardDishesAsync(rankedIds, ct)).ToDictionary(x => x.Id);
            // STEP-28: When mealKey exists, set _rankedMeals[mealKey].
            if (mealKey is not null) _rankedMeals[mealKey] = new(eligible, ranked, catalog);
            // STEP-29: Prepare actual through Select(filtered).Where(filtered).ToArray and distinct from actual.DistinctBy.
            var actual = rankedIds.Where(catalog.ContainsKey).Select(id => catalog[id])
                .Where(d => string.Equals(d.Restaurant.ProvinceName?.Trim(), state.Province!.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    (namedDishIds.Length > 0 || FitsMeal(d, mealSlot)) && (!restaurantId.HasValue || d.RestaurantId == restaurantId) &&
                    (namedDishIds.Length == 0 || namedDishIds.Contains(d.Id)) && mealTags.All(id => d.DishTags.Any(t => t.TagId == id))).ToArray();
            // Catalog có cùng một món ở nhiều quán với cùng vector nên điểm gần như bằng nhau: chỉ giữ bản xếp hạng cao nhất
            // của mỗi tên. Khi chia theo bữa, ưu tiên tên chưa dùng ở bữa trước (OrderBy ổn định nên vẫn giữ thứ tự ranking).
            var distinct = distinctNames ? actual.DistinctBy(d => AgentScopedPlanning.Normalize(d.Name)).ToArray() : actual;
            // STEP-30: When namedCenter exists, distinctNames is true and ranked.Scores exists, prepare scoreById as Dictionary<int, double>, repeat the inner loop and 1 more action(s).
            // Danh sách theo điểm đánh giá (không có score) không có căn cứ về khẩu vị để so "gần bằng nhau" nên giữ nguyên thứ tự.
            if (namedCenter is not null && distinctNames && ranked.Scores is { } rankedScores)
            {
                var scoreById = new Dictionary<int, double>();
                for (var index = 0; index < ranked.DishIds.Count; index++) scoreById.TryAdd(ranked.DishIds[index], rankedScores[index]);
                distinct = OrderNearby(distinct, scoreById, namedCenter);
            }
            // STEP-31: Prepare chosen.
            var chosen = distinct;
            // STEP-32: When diversify is true, set chosen.
            if (diversify)
                chosen = chosen.OrderBy(d => usedNames.Contains(AgentScopedPlanning.Normalize(d.Name)!) ? 1 : 0)
                    .Take(perMealSearch ? mealQuota : SplitPartQuota(requested, scope, input.TopK)).ToArray();
            // STEP-33: Prepare likelyPicks, then call usedNames.UnionWith.
            // Chỉ ghi nhận các món đầu, mỗi vị trí (ngày) của bữa một món, vì đó là món gần như chắc được chọn;
            // ghi cả danh sách thì phần sau bị đẩy xuống món xếp hạng thấp hoặc hết món mới.
            var likelyPicks = mealSlot is null ? chosen : chosen.Take(slots.Count(x => x.Meal == mealSlot));
            usedNames.UnionWith(likelyPicks.Select(d => AgentScopedPlanning.Normalize(d.Name)!));
            // STEP-34: When mealSlot exists, set chosenByMeal[mealSlot].
            if (mealSlot is not null) chosenByMeal[mealSlot] = chosen;
            if (mealSlot is not null) pools[mealSlot] = distinct;
            // STEP-35: Add the built entries to picked.
            picked.AddRange(chosen.Where(d => picked.All(x => x.Id != d.Id)));
            // STEP-36: Prepare constraints.
            // Grant là bằng chứng backend đã cấp các món này cho đúng ngày/bữa và điều kiện lọc.
            // Validator dựa vào grant để chặn model thêm/thay bằng ID chưa được cấp hoặc sai scope.
            var constraints = state with { TagIds = mealTags, ExcludeTagIds = excludeTags, MaxPricePerMeal = maxPrice, RadiusKm = place is null ? radius : state.RadiusKm };
            // STEP-37: Queue the built entry through context.Grants.Add.
            context.Grants.Add(new(scope.DayNumber, mealSlot, chosen.Select(x => x.Id).ToArray(), ranked.RequestId, constraints));
            // STEP-38: Prepare reserve through Where(filtered).Select(filtered).ToArray.
            // Phần còn lại của cùng danh sách reco (cùng request, cùng vector) không gửi cho model. Khi một ngày chỉ được cấp vài món
            // và model chọn trùng món đã dùng, backend lấy món chưa dùng xếp hạng cao nhất ở đây để thay, không tốn thêm lần gọi LLM.
            var reserve = distinct.Where(d => !chosen.Contains(d)).Select(x => x.Id).ToArray();
            // STEP-39: When reserve.Length > 0, call context.ReserveGrants.Add.
            if (reserve.Length > 0) context.ReserveGrants.Add(new(scope.DayNumber, mealSlot, reserve, ranked.RequestId, constraints));
            // STEP-40: Call telemetry.Record.
            telemetry.Record("ranking", context.ConversationId, context.TurnId, context.AttemptId, new { mode = ranked.RequestId.HasValue ? "personalized" : "rating_fallback",
                candidateCount = candidates.Count, ranked.RequestId, mealSlot, returnedCount = chosen.Length });
        }
        // STEP-41: Prepare coverage through RecordCoverage.
        // Bữa không có món vẫn được ghi mealCoverage = 0 để model/reply dựa trên bằng chứng thay vì đoán thiếu món.
        var coverage = RecordCoverage(context, state, scope, slot => perMealSearch
            ? chosenByMeal.GetValueOrDefault(slot)?.Length ?? 0 : picked.Count(dish => namedDishIds.Length > 0 || FitsMeal(dish, slot)), requiredTags);
        // STEP-42: Return the new result.
        return new(scope, perMealSearch, chosenByMeal, picked, requestIds, candidateTotal, ignoredTotal, coverage,
            slots.Count(x => state.MealSlots.Contains(x.Meal)), Pools: pools);
    }

    // RECO-05 nhận tối đa 2000 ứng viên; lấy rộng tới mức này trước khi gom theo tên rồi cắt theo Agent:MaxCandidates.
    private const int CandidatePool = 2000;
    private const int ReserveDepth = 30;
    private const int NeighbourDepth = 60;
    // Một bữa của một ngày bị tách khỏi phạm vi rộng hơn (khẩu vị riêng cho bữa sáng mọi ngày tách cả chuyến thành 9 phần): chỉ gửi model vài món.
    // Gửi đủ topK cho mỗi phần làm kết quả tool gần 80 nghìn ký tự và lượt sau vượt MaxContextCharacters (hội thoại 217). Phần còn lại vẫn ở
    // danh sách dự phòng, nên bước giữ các bữa gần nhau vẫn chọn sâu được và món nó chọn luôn có trong kết quả.
    private const int SplitSlotItems = 5;

    private static int SplitPartQuota(AgentScope requested, AgentScope part, int topK) =>
        requested != part && part.MealSlot is not null ? Math.Min(topK, SplitSlotItems) : topK;

    /// <summary>Ranking outcome of one homogeneous scope, or the trusted notice that stops the whole tool call.</summary>
    private sealed record ScopedRanking(AgentScope Scope, bool PerMeal, Dictionary<string, ANGI.Domain.Entities.Dish[]> ChosenByMeal,
        List<ANGI.Domain.Entities.Dish> Picked, List<Guid?> RequestIds, int CandidateCount, int IgnoredCount,
        AgentMealCoverage[] Coverage, int ExpectedMeals, object? Notice = null, Dictionary<string, ANGI.Domain.Entities.Dish[]>? Pools = null)
    {
        /// <summary>Wraps a catalog clarification so no partial candidates are returned with it.</summary>
        public static ScopedRanking Stopped(AgentScope scope, object notice) => new(scope, false, [], [], [], 0, 0, [], 0, notice);
    }

    /// <summary>Counts candidates compatible with each requested meal and replaces earlier observations for that slot.</summary>
    private static AgentMealCoverage[] RecordCoverage(AgentContext context, AgentState state, AgentScope scope,
        Func<string, int> available, string[] requiredTags)
    {
        // STEP-01: Prepare coverage through Where(filtered).Select(filtered).ToArray.
        var coverage = AgentScopedPlanning.Slots(state, scope)
            .Where(slot => state.MealSlots.Contains(slot.Meal) || scope.MealSlot == slot.Meal)
            .Select(slot => new AgentMealCoverage(slot.Day, slot.Meal, available(slot.Meal),
                AgentScopedPlanning.Fingerprint(state, slot.Day, slot.Meal), requiredTags)).ToArray();
        // STEP-02: For each meal in coverage, call context.MealCoverage.RemoveAll and call context.MealCoverage.Add.
        foreach (var meal in coverage)
        {
            context.MealCoverage.RemoveAll(previous => previous.DayNumber == meal.DayNumber && previous.MealSlot == meal.MealSlot);
            context.MealCoverage.Add(meal);
        }
        // STEP-03: Return coverage.
        return coverage;
    }

    /// <summary>One meal's filtered and ranked dishes, reused by the days of a long trip that share its conditions.</summary>
    private sealed record RankedMeal(ANGI.Domain.Entities.Dish[] Eligible, RecommendationResult Ranked, Dictionary<int, ANGI.Domain.Entities.Dish> Catalog);

    private async Task<object> SuggestResultAsync(AgentContext context, AgentSuggestionInput input, CancellationToken ct)
    {
        // STEP-01: Return the result of ArgumentError early when the price ceiling is a unit slip.
        if (input.MaxPrice is < AgentStateValidator.MinPlausiblePrice) return PriceUnitError(context, input.MaxPrice.Value);
        // STEP-02: Prepare suggestion, record what was searched and return the result.
        var suggestion = context.DishSuggestion = await SuggestAsync(context, input, ct);
        telemetry.Record("suggest_search", context.ConversationId, context.TurnId, context.AttemptId, new { input.MaxPrice, tags = input.TagIds?.Length ?? 0, input.Count, found = suggestion.Dishes.Length });
        return new { status = suggestion.Dishes.Length == 0 ? "nothing_found" : "suggested", suggestion.Personalized, suggestion.AroundPin, suggestion.HasPlan, suggestion.Area, suggestion.AreaNotFound,
            // Chữ về bữa ăn do backend điền vào ô {meals1}: đưa chữ này cho model thì nó viết lại lần nữa và câu trả lời bị lặp.
            dishes = suggestion.Dishes.Select(dish => new { dish.Name, dish.Restaurant, dish.Price, dish.Address, hasMeals = dish.Meals is not null }) };
    }

    /// <summary>
    /// Locates the places the model read from the user's words and stores them on the state; coordinates, radius and district are always the backend's.
    /// Over a plan only its existing days can move: adding days changes a saved duration, which needs the user's confirmation through the state.
    /// </summary>
    private async Task<object> LocatePlacesAsync(AgentContext context, AgentLocateInput input, CancellationToken ct)
    {
        // STEP-01: Return the result of ArgumentError early when places cannot be read here or the arguments break the contract.
        if (places is null || catalogSearch is null) return ArgumentError(context, "Hiện chưa định vị được địa điểm.");
        var hasPlan = AgentIntakePolicy.HasPlan(context);
        var tripArea = string.IsNullOrWhiteSpace(input.TripArea) ? null : input.TripArea.Trim();
        var stops = input.Stops ?? [];
        if (hasPlan && tripArea is not null)
            return ArgumentError(context, "Lịch đã có: dùng stops cho từng ngày cần đổi, kể cả khi đổi cả chuyến. Chưa rõ ngày nào thì hỏi lại bằng clarification=edit_scope.");
        if (tripArea is null && stops.Length == 0 || tripArea?.Length > MaxPlaceName || stops.Length > AgentStateValidator.MaxDays ||
            stops.Any(stop => stop.Days is not { Length: > 0 } || stop.Place?.Length > MaxPlaceName || stop.Days.Any(day => day is < 1 or > AgentStateValidator.MaxDays)))
            return ArgumentError(context, "locate_places cần tripArea hoặc stops; mỗi stop có days từ 1 đến số ngày tối đa và tên nơi không quá dài.");
        // STEP-02: Over a plan, keep only the days it has, and name the others so the model can ask about extending the trip.
        var outsideTrip = hasPlan ? stops.SelectMany(stop => stop.Days!).Where(day => day > context.State.NumDays).Distinct().ToArray() : [];
        if (hasPlan) stops = stops.Select(stop => stop with { Days = stop.Days!.Where(day => day <= context.State.NumDays).ToArray() }).Where(stop => stop.Days!.Length > 0).ToArray();
        if (hasPlan && stops.Length == 0) return new { status = "outside_trip", itineraryChangedDays = Array.Empty<short>(), outsideTrip };
        // STEP-03: Load provinces and locate the places.
        var provinces = await catalogSearch.GetProvincesAsync(ct);
        var trip = await places.LocateAsync(tripArea, stops, context.State, provinces, ct);
        // STEP-04: Store what was found, keep the note the user sees with the next question, and over a plan record the days that really moved.
        var before = AgentJson.Write(context.State.DayAreas);
        context.State = AgentPlaceResolver.Apply(context.State, trip);
        context.PlaceNote = AgentPlaceResolver.Note(context.State, trip);
        if (hasPlan && AgentJson.Write(context.State.DayAreas) != before)
            context.MovedDays = trip.DayAreas.Select(area => area.DayNumber).Concat(trip.WholeProvinceDays).Distinct().ToArray();
        // Nơi đến đã lưu từ lượt trước nhưng lịch chưa được xếp lại theo nó (lượt đó lỗi giữa chừng): so với món đang có trong lịch,
        // không chỉ với state cũ, để người dùng không phải nói lại hành trình.
        if (hasPlan)
            context.MovedDays = context.MovedDays.Concat(trip.DayAreas.Select(area => area.DayNumber).Where(day => OutsideArea(context, day))).Distinct().Order().ToArray();
        telemetry.Record("locate_places", context.ConversationId, context.TurnId, context.AttemptId, new { days = trip.DayAreas.Length, tripArea = trip.TripArea is not null, unresolved = trip.Unresolved.Length, moved = context.MovedDays.Length });
        // STEP-05: Return what the backend understood.
        return new
        {
            status = trip.DayAreas.Length > 0 || trip.TripArea is not null || trip.WholeProvinceDays.Length > 0 ? "located" : "not_found",
            context.State.Province, context.State.NumDays, trip.Unresolved, trip.WholeProvinceDays, trip.DayAreas,
            tripArea = trip.TripArea?.Label, catalogProvinces = provinces, itineraryChangedDays = context.MovedDays, outsideTrip
        };
    }

    /// <summary>
    /// Picks dishes for "gợi ý một món": ranked by the user's taste around their area when one is known, across the catalog otherwise, because a place is not
    /// needed to name a dish. Nothing is granted and no state changes, and a dish already suggested in this conversation is left out so "món khác" moves on.
    /// </summary>
    public async Task<AgentDishSuggestion> SuggestAsync(AgentContext context, AgentSuggestionInput input, CancellationToken ct)
    {
        // STEP-01: Prepare state, locate the place the user named, then load catalogTags through tags.GetTagsAsync.
        var state = context.State;
        var (named, areaNotFound) = await SuggestionAreaAsync(context, input, ct);
        var mealAsked = AgentStateValidator.Meals.Contains(input.MealSlot);
        var catalogTags = await tags.GetTagsAsync(null, ct);
        // STEP-02: Prepare known, wanted, categories, main and 4 more, then load pool through dishes.GetCandidateIdsAsync.
        var known = catalogTags.Select(tag => tag.Id).ToHashSet();
        var wanted = (input.TagIds ?? []).Where(known.Contains).Distinct().ToArray();
        // Không nói loại món thì gợi ý món chính, như các bữa của lịch; nói rõ đồ uống hay ăn vặt thì theo đó.
        var categories = catalogTags.Where(tag => tag.TagType == ANGI.Domain.Enums.TagType.Category).Select(tag => tag.Id).ToHashSet();
        var main = catalogTags.FirstOrDefault(tag => tag.TagType == ANGI.Domain.Enums.TagType.Category && tag.Code == AgentScopedPlanning.MainTagCode);
        var required = main is not null && !wanted.Any(categories.Contains) ? wanted.Append(main.Id).ToArray() : wanted;
        var otherProvince = !string.IsNullOrWhiteSpace(input.Province) && !string.Equals(input.Province.Trim(), state.Province?.Trim(), StringComparison.OrdinalIgnoreCase);
        // Nơi người dùng vừa nêu cho lần gợi ý này được ưu tiên hơn khu của chuyến đi.
        var area = named is not null ? new NearbyArea(named.Latitude, named.Longitude, named.RadiusKm) : otherProvince ? null : AgentScopedPlanning.Area(state, 0);
        var filter = new DishCandidateFilter { Province = named?.Province ?? (otherProvince ? input.Province!.Trim() : state.Province), TagIds = required,
            ExcludeTagIds = state.ExcludeTagIds.Concat((input.ExcludeTagIds ?? []).Where(known.Contains)).Distinct().ToArray(),
            MaxPrice = Min(state.MaxPricePerMeal, input.MaxPrice), Area = area, RequireAllTags = true,
            CompatibleMealTimeCode = AgentStateValidator.Meals.Contains(input.MealSlot) ? input.MealSlot : null };
        var pool = await dishes.GetCandidateIdsAsync(filter, Math.Max(limits.MaxCandidates, CandidatePool), ct);
        // STEP-03: Prepare cards from pool.ToArray, qualified through DistinctBy(filtered).ToArray, shown from context.History.Where and more.
        var cards = pool.Count == 0 ? new Dictionary<int, ANGI.Domain.Entities.Dish>() : (await dishes.GetCardDishesAsync(pool.ToArray(), ct)).ToDictionary(dish => dish.Id);
        var qualified = pool.Where(cards.ContainsKey).Select(id => cards[id]).Where(dish => required.All(id => dish.DishTags.Any(tag => tag.TagId == id)) && FitsMeal(dish, filter.CompatibleMealTimeCode))
            .DistinctBy(dish => AgentScopedPlanning.Normalize(dish.Name)).ToArray();
        // Món đã có trong lịch cũng không gợi ý lại; hết món khác thì mới dùng tới chúng.
        var shown = AgentScopedPlanning.Normalize(string.Join(' ', context.History.Where(turn => turn.Role == "assistant").Select(turn => turn.Content)
            .Concat((context.Source?.Items ?? []).Select(item => item.DishName))))!;
        var fresh = qualified.Where(dish => !shown.Contains(AgentScopedPlanning.Normalize(dish.Name)!, StringComparison.Ordinal)).ToArray();
        var candidates = (fresh.Length > 0 ? fresh : qualified).Take(limits.MaxCandidates).ToArray();
        // STEP-04: Return AgentDishSuggestionPolicy.Suggestion early when candidates.Length is 0.
        var aroundPin = named is null && area is not null && state.OriginMode == "pinned";
        var hasPlan = AgentIntakePolicy.HasPlan(context);
        if (candidates.Length == 0) return AgentDishSuggestionPolicy.Suggestion([], aroundPin, false, hasPlan, named?.Label, areaNotFound, mealAsked);
        // STEP-05: Check the rule in the computed value.EnsureAsync.
        var taste = await (preferences ?? new AgentPreferenceService(recommendation)).EnsureAsync(state, context.UserId, ct);
        // STEP-06: Prepare count through Math.Clamp, ids through candidates.Select(filtered).ToArray, targets, vector from AgentTasteOntology.Apply, result, ranked and order.
        var count = Math.Clamp(input.Count, 1, AgentDishSuggestionPolicy.MaxCount);
        var ids = candidates.Select(dish => dish.Id).ToArray();
        // Khẩu vị nói trong câu gợi ý chỉ áp cho lần xếp hạng này, đè lên khẩu vị của chuyến đi và không ghi vào state.
        var targets = state.TasteTargets.Concat((input.TasteTargets ?? []).Where(target => AgentTasteOntology.Scale.Contains(target.Target))).ToArray();
        var vector = taste.Preferences?.Initialized == true ? AgentTasteOntology.Apply(taste.Preferences, targets).EffectiveVector : null;
        // Reco is asked for more than will be shown, so dishes of the same restaurant can be skipped and the list still fills.
        var result = taste.Preferences?.Initialized == true
            ? await recommendation.RecommendConversationAsync(context.UserId, ids, Math.Min(ids.Length, count * 3), new RecommendationVector("conversation", vector), ct) : null;
        var ranked = result?.DishIds.Where(ids.Contains).ToArray() ?? [];
        var order = ranked.Length > 0 ? ranked : ids;
        // STEP-07: Prepare personalized, then return the result of AgentDishSuggestionPolicy.Suggestion.
        // Chỉ tính là theo khẩu vị khi người dùng có hồ sơ khẩu vị và reco xác nhận đã xếp bằng vector: reco bỏ qua vector của người chưa có tương tác nào, nên khẩu vị vừa nói một mình chưa đủ.
        var personalized = taste.Preferences?.BaselineVector?.Any(value => value != 0) == true && vector is not null && ranked.Length > 0 && result?.VectorSource == "conversation";
        // Rank order, at most MaxPerRestaurant dishes of one restaurant; only when that leaves the list short are the skipped dishes used, still in rank order.
        var ordered = order.Select(id => candidates.First(dish => dish.Id == id)).ToArray();
        var perRestaurant = new Dictionary<long, int>();
        var varied = ordered.Where(dish => (perRestaurant[dish.RestaurantId] = perRestaurant.GetValueOrDefault(dish.RestaurantId) + 1) <= AgentDishSuggestionPolicy.MaxPerRestaurant).Take(count).ToList();
        varied.AddRange(ordered.Where(dish => !varied.Contains(dish)).Take(count - varied.Count));
        return AgentDishSuggestionPolicy.Suggestion(varied, aroundPin, personalized, hasPlan, named?.Label, areaNotFound, mealAsked);
    }

    /// <summary>Locates the place a suggestion was asked around; a place that cannot be located is reported rather than silently ignored, and a province is a filter of its own.</summary>
    private async Task<(AgentPlaceMatch? Named, string? NotFound)> SuggestionAreaAsync(AgentContext context, AgentSuggestionInput input, CancellationToken ct)
    {
        // STEP-01: Return no area early when none was named or places cannot be read here.
        if (places is null || catalogSearch is null || string.IsNullOrWhiteSpace(input.Area)) return (null, null);
        // STEP-02: Prepare name and provinces, then return no area early when the name is a province.
        var name = input.Area.Trim();
        var provinces = await catalogSearch.GetProvincesAsync(ct);
        if (AgentPlaceResolver.IsProvince(name, provinces)) return (null, null);
        // STEP-03: Return what the place search found.
        var match = name.Length > MaxPlaceName ? null : await places.LocateAsync(name, input.Province ?? context.State.Province, true, provinces, ct);
        return match is null ? (null, name[..Math.Min(name.Length, MaxPlaceName)]) : (match, null);
    }

    private async Task<object> SearchAsync(AgentContext context, AgentCatalogInput input, CancellationToken ct)
    {
        // STEP-01: Return the result of ArgumentError early when input.Kind is not ("dish" or "restaurant"), input.Name is blank, input.Name.Length > 200 or 2 more condition(s).
        if (input.Kind is not ("dish" or "restaurant") || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200 ||
            input.Province is not null && (string.IsNullOrWhiteSpace(input.Province) || input.Province.Length > 100) || input.SelectedId is <= 0)
            return ArgumentError(context, "Tên món/quán cần tra cứu không hợp lệ.");
        // STEP-02: Return the result of ArgumentError early when IsPointingOnly(input.Name) holds.
        // "cái này", "món đó" chỉ là từ chỉ trỏ, không phải tên: tra cứu sẽ báo "không tìm thấy" một món người dùng chưa từng nêu.
        if (IsPointingOnly(input.Name))
            return ArgumentError(context, "Đây là từ chỉ trỏ, không phải tên món/quán. Hãy hỏi người dùng muốn nói món/bữa nào (clarification=edit_scope).");
        // STEP-03: Return the result of ArgumentError early when AgentDishSuggestionPolicy.IsGenericName(input.Name) holds.
        // "món ăn bất kì" không phải tên món: tra nó sẽ báo "không tìm thấy" thay vì gợi ý món người dùng đang xin.
        if (AgentDishSuggestionPolicy.IsGenericName(input.Name))
            return ArgumentError(context, "Đây không phải tên món/quán. Người dùng đang xin gợi ý: hãy gọi suggest_dish.");
        // STEP-04: Return the result of ArgumentError early when AgentPlaceResolver.IsKnownPlace(context.State, input.Name) holds.
        // Nơi đến đã đọc được từ hành trình không phải tên quán: tra nó sẽ báo "không tìm thấy" cho một địa điểm backend đã biết.
        if (AgentPlaceResolver.IsKnownPlace(context.State, input.Name))
            return ArgumentError(context, "Đây là nơi đến trong hành trình của người dùng, không phải tên món/quán; backend đã tìm quanh nơi này. Hãy gọi get_recommendation.");
        // STEP-05: Call LookupAsync and keep result.
        var result = await LookupAsync(context, input, ct);
        // STEP-06: When result.Status is "found", set context.CatalogAnswer; otherwise set context.ToolNotice.
        // Tìm thấy đúng tên chỉ là dữ kiện để trả lời câu hỏi về catalog, không phải câu hỏi cho người dùng: nếu gán ToolNotice,
        // mọi câu clarify của lượt sẽ thành "Mình tìm thấy đúng món/quán bạn hỏi trên ANGI."
        if (result.Status == "found") context.CatalogAnswer = Describe(result);
        // Giữ riêng kết quả tra cứu không khớp (kèm các lựa chọn gần đúng): lần lấy gợi ý sau xóa ToolNotice, làm câu hỏi
        // "chọn trong kết quả đã tìm thấy" mất danh sách và "không tìm thấy" mất tên món/quán.
        // Nhóm món là dữ kiện cho model, không phải câu hỏi chọn món cho người dùng.
        else if (result.Status != "family") context.ToolNotice = context.CatalogNotice = Describe(result);
        // STEP-07: Return the anonymous result.
        return new { result.Status, result.Message, result.Matches };
    }

    /// <summary>Remembers that this name needs a choice from the user, then returns the lookup unchanged.</summary>
    private static CatalogLookup Ambiguous(AgentContext context, string name, CatalogLookup lookup)
    {
        context.AmbiguousNames.Add(name);
        return lookup;
    }

    private async Task<CatalogLookup> LookupAsync(AgentContext context, AgentCatalogInput input, CancellationToken ct)
    {
        // STEP-01: Throw the error from Invalid when input.Kind is not ("dish" or "restaurant"), input.Name is blank, input.Name.Length > 200 or 2 more condition(s).
        if (input.Kind is not ("dish" or "restaurant") || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200 ||
            input.Province is not null && (string.IsNullOrWhiteSpace(input.Province) || input.Province.Length > 100) || input.SelectedId is <= 0)
            throw Invalid("Tên món/quán cần tra cứu không hợp lệ.");
        // STEP-02: Throw the error from Invalid when catalogSearch is missing.
        if (catalogSearch is null) throw Invalid("Tra cứu catalog chưa được cấu hình.");
        // STEP-03: Load matches through catalogSearch.SearchAsync.
        var matches = await catalogSearch.SearchAsync(input.Kind, input.Name, input.Province, 21, ct);
        // STEP-04: Prepare exact through matches.Where(filtered).ToArray.
        var exact = matches.Where(x => AgentScopedPlanning.Normalize(x.Name) == AgentScopedPlanning.Normalize(input.Name)).ToArray();
        // STEP-05: When input.SelectedId has a value, keep only that result of this search.
        // A branch is usually named longer than the name the user gave ("Cháo Súp Linh Hoa - Trần Khát Chân" for "Cháo Súp Linh Hoa").
        // Such a longer-named result counts only once the user was asked to choose for this name (state.CatalogChoices), so the model
        // can never pick a branch on its own; an exact-name result always counts.
        var name = AgentScopedPlanning.Normalize(input.Name)!;
        if (input.SelectedId.HasValue)
        {
            var exactPick = exact.Where(x => x.Id == input.SelectedId).ToArray();
            var asked = context.State.CatalogChoices?.Contains(name) == true;
            exact = exactPick.Length > 0 ? exactPick : asked ? matches.Where(x => x.Id == input.SelectedId).ToArray() : [];
        }
        // STEP-06: Return the new result early when input.SelectedId has a value and exact.Length is 0.
        if (input.SelectedId.HasValue && exact.Length == 0)
            return Ambiguous(context, name, new("ambiguous", "Lựa chọn chưa khớp kết quả tra cứu đúng tên. Bạn muốn chọn kết quả nào trong danh sách?", matches.Take(20).ToArray()));
        // STEP-07: When exact.Length is 0, prepare elsewhere from catalogSearch.SearchAsync, prepare outside through elsewhere.Any and 1 more action(s).
        if (exact.Length == 0)
        {
            // Tên món chung ("cà ri", "bún") khớp nhiều món khác tên: đó là một nhóm món, không phải câu hỏi chọn món.
            // Bước xếp hạng chọn món hợp khẩu vị và gần các bữa khác nhất; người dùng muốn món khác thì đổi sau.
            if (input.Kind == "dish" && !input.SelectedId.HasValue && Family(matches, input.Name) is { Length: > 0 } family)
                return family.Any(x => x.Available)
                    ? new("family", $"Có {family.Length} món thuộc nhóm “{AgentReplyBuilder.Safe(input.Name)}”. Người dùng chưa nêu món cụ thể thì giữ nguyên tên nhóm trong mealRequests.dishName: backend chọn món hợp khẩu vị và gần các bữa khác nhất, không cần hỏi chọn.", family)
                    : new("unavailable", "Món/quán có trong hệ thống nhưng hiện chưa khả dụng. Bạn có muốn xem lựa chọn khác không?", family);
            var elsewhere = input.Province is null ? [] : await catalogSearch.SearchAsync(input.Kind, input.Name, null, 21, ct);
            var outside = elsewhere.Any(x => AgentScopedPlanning.Normalize(x.Name) == AgentScopedPlanning.Normalize(input.Name));
            if (!outside && matches.Length > 0) context.AmbiguousNames.Add(name);
            return new(outside ? "outside_province" : matches.Length > 0 ? "ambiguous" : "not_found",
                outside ? "Có kết quả cùng tên trong hệ thống nhưng ngoài tỉnh đã chọn. Bạn muốn giữ địa điểm hay thay đổi?"
                : matches.Length > 0 ? "Chưa xác định được đúng món/quán theo tên đã yêu cầu. Bạn muốn chọn kết quả nào trong danh sách?"
                : $"Chưa tìm thấy món/quán theo tên yêu cầu (“{AgentReplyBuilder.Safe(input.Name)}”) trong danh mục ANGI. Bạn có muốn xem lựa chọn khác không?", matches.Take(20).ToArray());
        }
        // STEP-08: Return the new result early when (input.Kind is "restaurant" and exact.Length is not 1) or exact.Length > 20.
        if (input.Kind == "restaurant" && exact.Length != 1 || exact.Length > 20)
            return Ambiguous(context, name, new("ambiguous", "Có nhiều kết quả cùng tên. Bạn muốn chọn nhà hàng/chi nhánh nào trong danh sách?", exact.Take(20).ToArray()));
        // STEP-09: Return the new result early when exact.Any does not match the filter.
        if (!exact.Any(x => x.Available))
            return new("unavailable", "Món/quán có trong hệ thống nhưng hiện chưa khả dụng. Bạn có muốn xem lựa chọn khác không?", exact);
        // STEP-10: Return the new result.
        return new("found", "Mình tìm thấy đúng món/quán bạn hỏi trên ANGI.", exact);
    }

    /// <summary>Returns a model-argument error to the model as a tool result so it can correct the call; never a user-visible notice.</summary>
    /// <summary>Sends a price written in thousands back to the model; run as given it would only report that nothing fits.</summary>
    private object PriceUnitError(AgentContext context, decimal price) =>
        ArgumentError(context, $"Giá tối đa {price} quá thấp. Tiền tính bằng đồng: 50k là 50000. Hãy gọi lại với đúng số đồng.");

    private object ArgumentError(AgentContext context, string message)
    {
        telemetry.Record("tool_argument_error", context.ConversationId, context.TurnId, context.AttemptId, new { message });
        return new { status = "invalid_arguments", rankingMode = "none", requestId = (Guid?)null, items = Array.Empty<object>(), warnings = new[] { message } };
    }

    private static object Notice(AgentContext context, string message, string status)
    {
        context.ToolNotice = message;
        return new { status, rankingMode = "none", requestId = (Guid?)null, items = Array.Empty<object>(), warnings = new[] { message } };
    }

    /// <summary>A catalog lookup notice that later ranking calls cannot erase, so catalog questions keep the name and the near matches.</summary>
    private static object CatalogNotice(AgentContext context, string message, string status)
    {
        context.CatalogNotice = message;
        return Notice(context, message, status);
    }

    private static bool FitsMeal(ANGI.Domain.Entities.Dish dish, string? meal) => meal is null ||
        !dish.DishTags.Any(x => x.Tag.TagType == ANGI.Domain.Enums.TagType.MealTime) ||
        dish.DishTags.Any(x => x.Tag.TagType == ANGI.Domain.Enums.TagType.MealTime && x.Tag.Code == meal);

    private sealed record CatalogLookup(string Status, string Message, AgentCatalogMatch[] Matches);

    /// <summary>
    /// Catalog dishes whose name holds every word of the requested name, in order ("cà ri" → "Cơm Cà Ri Nhật Gà Chiên"); accents are compared
    /// first, and without accents only when nothing matches with them. A name of one short word is too broad to stand for a family.
    /// </summary>
    private static AgentCatalogMatch[] Family(IReadOnlyList<AgentCatalogMatch> matches, string name)
    {
        var accented = matches.Where(x => AgentScopedPlanning.FitsName(name, x.Name, ignoreAccents: false)).ToArray();
        return accented.Length > 0 ? accented : matches.Where(x => AgentScopedPlanning.FitsName(name, x.Name)).ToArray();
    }

    private static string Describe(CatalogLookup lookup) => lookup.Message + (lookup.Matches.Length == 0 ? "" : "\n" +
        string.Join("\n", lookup.Matches.Take(4).Select(x => $"{x.Name} — {x.RestaurantName}, {x.Address}, {x.Province}; " +
            (x.Available ? "khả dụng" : "chưa khả dụng"))));

    /// <summary>Resolves every coordinate from issued candidate IDs or unchanged source items.</summary>
    private async Task<object> RouteAsync(AgentContext context, AgentRouteInput input, CancellationToken ct)
    {
        // STEP-01: Return the result of ArgumentError early when context.State.NumDays has no value, input.DayNumber < 1, input.DayNumber > context.State.NumDays or 5 more condition(s).
        // Preview chưa có roadmap/day ID nên nhận danh sách itemKey + dishId theo thứ tự muốn đi.
        // Tối đa 24 món để còn chỗ cho origin trong giới hạn 25 điểm của route adapter.
        if (!context.State.NumDays.HasValue || input.DayNumber < 1 || input.DayNumber > context.State.NumDays || input.OrderedItems is null || input.OrderedItems.Length > 24 ||
            input.OrderedItems.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemKey) || x.ItemKey.Length > 100 || x.DishId <= 0) ||
            input.OrderedItems.Select(x => x.ItemKey).Distinct().Count() != input.OrderedItems.Length || input.Profile is not ("driving" or "walking" or "cycling"))
            return ArgumentError(context, "Tham số tuyến không hợp lệ (ngày, danh sách món hoặc phương tiện).");
        // STEP-02: Prepare allowed through SelectMany(filtered).Concat().ToHashSet.
        // Chỉ nhận ID từ candidates đã cấp cho ngày này hoặc từ các món nguồn thuộc ngày đó.
        // Không cho model gửi tọa độ tùy ý; tọa độ luôn tra từ quán của dishId trong catalog.
        var allowed = context.Grants.Where(g => !g.DayNumber.HasValue || g.DayNumber == input.DayNumber).SelectMany(g => g.DishIds)
            .Concat((context.Source?.Items ?? []).Where(x => x.DayNumber == input.DayNumber).Select(x => x.DishId)).ToHashSet();
        // STEP-03: Return the result of ArgumentError early when input.OrderedItems.Any matches the filter.
        if (input.OrderedItems.Any(x => !allowed.Contains(x.DishId))) return ArgumentError(context, "Tuyến chứa món chưa được backend cấp cho ngày này.");
        // STEP-04: Prepare catalog through dishes.GetCardDishesAsync().ToDictionary.
        var catalog = (await dishes.GetCardDishesAsync(input.OrderedItems.Select(x => x.DishId).Distinct().ToArray(), ct)).ToDictionary(x => x.Id);
        // STEP-05: Return the result of ArgumentError early when input.OrderedItems.Any matches the filter.
        if (input.OrderedItems.Any(x => !catalog.ContainsKey(x.DishId))) return ArgumentError(context, "Món trong tuyến không còn khả dụng.");
        // STEP-06: Prepare items through OrderedItems.Select(filtered).ToArray.
        // "lunch" chỉ là giá trị tạm để dựng AgentItem cho route; thứ tự chặng theo OrderedItems.
        // Không dùng giá trị bữa tạm này để thay lịch proposal hoặc ghi roadmap.
        var items = input.OrderedItems.Select((x, i) => new AgentItem(x.ItemKey, null, input.DayNumber, "lunch", (short)i, x.DishId,
            Latitude: catalog[x.DishId].Restaurant.Latitude, Longitude: catalog[x.DishId].Restaurant.Longitude)).ToArray();
        // STEP-07: Return the result of routes.EstimateAsync.
        // Tái sử dụng RM-14 qua AgentRouteEstimator; trả rõ cờ estimate/origin bị bỏ qua nếu có.
        return await routes.EstimateAsync(context.State, input.DayNumber, items, ParseProfile(input.Profile), ct);
    }

    /// <summary>Returns only the roadmap already bound to and owned by the current conversation.</summary>
    // Roadmap đã được ContextBuilder nạp sau kiểm tra ownership. Tool chỉ trả đúng roadmap đó.
    // Viết "sửa roadmap 88" trong chat không tự gắn conversation vào 88; frontend phải mở đúng context.
    // Hiện chưa có tool liệt kê/tìm/chuyển sang roadmap khác chỉ bằng lời chat.
    // ID khác (hoặc hội thoại chưa gắn roadmap) không trả dữ liệu nào; model nhận lỗi tham số để dùng bản xem trước trong ngữ cảnh,
    // thay vì làm hỏng cả lượt chat thành 404 như trước.
    private object GetRoadmap(AgentContext context, AgentRoadmapInput input) =>
        input.RoadmapId > 0 && input.RoadmapId == context.Roadmap?.Id ? AgentJson.FromRoadmap(context.Roadmap, context.State)
            : ArgumentError(context, context.Roadmap is null
                ? "Hội thoại này chưa gắn roadmap đã lưu; lịch hiện tại là bản xem trước có sẵn trong ngữ cảnh."
                : "Chỉ đọc được roadmap đang gắn với hội thoại này.");

    /// <summary>Combines optional upper bounds without relaxing an existing bound.</summary>
    // Một bên chưa đặt giới hạn thì dùng bên còn lại; cả hai có thì giữ mức chặt hơn.
    private static decimal? Min(decimal? left, decimal? right) => left.HasValue && right.HasValue ? Math.Min(left.Value, right.Value) : left ?? right;

    /// <summary>
    /// Orders dishes when the user named a place: taste stays the main criterion, and among dishes that fit about equally the nearer restaurant comes first.
    /// Only that case uses it; a whole-province search, GPS and an origin on the map keep their plain ranking.
    /// Keeps the ranking between score bands measured from the best dish, and sorts by distance inside each band.
    /// Dishes without a score keep their place at the end of the order they arrived in.
    /// </summary>
    public static ANGI.Domain.Entities.Dish[] OrderNearby(IReadOnlyList<ANGI.Domain.Entities.Dish> ranked, IReadOnlyDictionary<int, double> scores, NearbyArea center)
    {
        // STEP-01: Prepare known through ranked.Where(filtered).ToArray.
        var known = ranked.Where(dish => scores.ContainsKey(dish.Id)).ToArray();
        // STEP-02: Return the result of ranked.ToArray early when known.Length is 0.
        if (known.Length == 0) return ranked.ToArray();
        // STEP-03: Prepare best through known.Max.
        var best = known.Max(dish => scores[dish.Id]);
        // STEP-04: Return the result of ThenBy(filtered).Concat().ToArray.
        return known.OrderBy(dish => (int)Math.Floor((best - scores[dish.Id]) / NearlyEqualScore)).ThenBy(dish => Distance(dish, center))
            .Concat(ranked.Where(dish => !scores.ContainsKey(dish.Id))).ToArray();
    }

    /// <summary>Among branches selling the same dish, which fit the same taste, keeps the nearest; the first branch's position in the list is kept.</summary>
    public static IEnumerable<ANGI.Domain.Entities.Dish> NearestBranches(IEnumerable<ANGI.Domain.Entities.Dish> dishes, Func<ANGI.Domain.Entities.Dish, string?> name, NearbyArea center) =>
        dishes.GroupBy(name).Select(branches => branches.MinBy(dish => Distance(dish, center))!);

    private static double Distance(ANGI.Domain.Entities.Dish dish, NearbyArea center) =>
        NearbyArea.DistanceKm(center.Latitude, center.Longitude, dish.Restaurant.Latitude, dish.Restaurant.Longitude);

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
    int[]? ExcludeDishIds = null, decimal? MaxPricePerMeal = null, decimal? RadiusKm = null, int TopK = 50, bool Alternative = false,
    decimal? MaxLegKm = null);

/// <summary>What a dish suggestion is limited to; every value comes from the user's own request.</summary>
public sealed record AgentSuggestionInput(string? Province = null, string? MealSlot = null, short[]? TagIds = null, short[]? ExcludeTagIds = null, decimal? MaxPrice = null, int Count = 1, AgentTasteTarget[]? TasteTargets = null, string? Area = null);

/// <summary>The places of a new trip as the model read them; a stop with no place is a day with no fixed place.</summary>
public sealed record AgentLocateInput(string? TripArea = null, AgentLocateStop[]? Stops = null);

/// <summary>One or more days of a trip and where the traveler will be.</summary>
public sealed record AgentLocateStop(short[]? Days = null, string? Place = null);

/// <summary>Ordered unsaved preview route request.</summary>
public sealed record AgentRouteInput(short DayNumber, AgentRouteItem[] OrderedItems, string Profile = "driving");

/// <summary>References a backend-issued dish using an opaque preview key.</summary>
public sealed record AgentRouteItem(string ItemKey, int DishId);

/// <summary>References the single owned roadmap attached to a conversation.</summary>
public sealed record AgentRoadmapInput(long RoadmapId);
