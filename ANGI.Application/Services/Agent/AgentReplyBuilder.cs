using System.Globalization;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Builds factual responses from backend snapshots and fixed question intents, never provider prose.</summary>
public static class AgentReplyBuilder
{
    // Không mời gửi lại y nguyên: cùng dữ liệu và cùng yêu cầu thường lặp lại đúng kết quả nhưng vẫn tốn lượt gọi AI.
    public const string PlanningIncomplete = "Mình chưa hoàn tất việc sắp xếp lịch theo yêu cầu của bạn. " +
        "Bạn có thể nói rõ hơn hoặc điều chỉnh yêu cầu, ví dụ ngày/bữa, món muốn ăn, ngân sách hoặc khẩu vị.";
    public const string SubstitutionNote = "món được hệ thống chọn lại từ chính danh sách gợi ý đã lấy để đúng ngày/bữa hoặc tránh lặp món; " +
        "các món này vẫn theo khẩu vị, ngân sách và điều kiện bạn đã nêu.";
    // Chỉ còn lặp khi cả danh sách gợi ý của bữa đó (kể cả món dự phòng) không còn món khác tên phù hợp; không suy ra catalog thiếu món.
    public const string RepeatNote = "Một số món xuất hiện ở nhiều bữa vì danh sách gợi ý phù hợp cho các bữa đó không còn món khác.";
    public const string NoveltyQuestion = "Bạn muốn món khác các món trong lịch hiện tại, hay món bạn chưa từng ăn? ANGI chưa có đủ dữ liệu để xác nhận toàn bộ lịch sử ăn uống của bạn.";
    // ANGI không biết người dùng đã ăn gì; chỉ đề nghị điều backend làm được thật (khác các món trong lịch hiện tại).
    public const string NoveltyWithoutHistory = "ANGI chưa có dữ liệu về những món bạn đã ăn nên chưa thể chọn theo ý \"món chưa từng ăn\". " +
        "Mình có thể chọn các món khác với lịch hiện tại. Bạn có muốn vậy không?";
    // Chưa có lịch thì không có "lịch hiện tại" để tránh: lập lịch theo khẩu vị như bình thường.
    public const string NoveltyWithoutHistoryOrPlan = "ANGI chưa có dữ liệu về những món bạn đã ăn nên chưa thể chọn theo ý \"món chưa từng ăn\". " +
        "Mình sẽ lập lịch theo các món hợp khẩu vị của bạn.";
    private const int MaxNamedChanges = 3;
    public const string RatingFallbackNote = "Một số món được xếp theo điểm đánh giá vì mình chưa đủ dữ liệu về khẩu vị của bạn.";
    public const string TasteNote = "Mình ưu tiên theo khẩu vị bạn chọn, nhưng chưa thể bảo đảm từng món đúng mức vị đó.";
    public const string NothingToSave = "Hiện chưa có lịch nào để lưu. Bạn cho mình biết muốn ăn ở đâu, mấy ngày để mình lập lịch nhé.";
    public const string AnswerUnavailable = "Mình chưa trả lời cụ thể được câu này. Bạn nhắn ‘xem lịch’ để xem lại, hoặc cho mình biết muốn đổi ngày, bữa hay món nào nhé.";
    public static readonly string[] Clarifications = ["trip_details", "taste_scope", "taste_intensity", "edit_scope",
        "novelty", "catalog_choice", "coverage", "catalog_support", "budget", "general"];

    /// <summary>
    /// Announces a preview in a few sentences: what changed from <paramref name="source"/> when something was kept, otherwise the new plan.
    /// Its dishes are shown by the proposal card and map, not repeated in chat. The opener is friendly wording that states no fact.
    /// </summary>
    public static string Proposal(AgentSnapshot snapshot, int substituted = 0, AgentSnapshot? source = null, string? opener = null)
    {
        var lines = new List<string> { $"{opener ?? "Xong rồi!"} {Summary(snapshot, source)}" };
        // Thẻ đề xuất chỉ liệt kê món đã có, nên bữa còn trống phải được nói rõ ở đây.
        if (EmptyMeals(snapshot) is { Length: > 0 } empty) lines.Add($"Còn thiếu món cho {string.Join("; ", empty)}.");
        if (snapshot.Items.Any(x => !x.RequestId.HasValue)) lines.Add(RatingFallbackNote);
        // Nêu rõ mức và phạm vi đã áp dụng để người dùng thấy ngay nếu hệ thống hiểu sai ngày/bữa.
        if (TasteScopes(snapshot.State) is { } tastes) lines.Add($"Khẩu vị áp dụng: {tastes}.");
        // Món/quán người dùng gọi tên được xếp vào bữa nào thì nói rõ, để họ biết có thể yêu cầu đổi sang bữa khác.
        foreach (var request in snapshot.State.MealRequests.Where(x => x.DayNumber.HasValue && x.MealSlot is not null && (x.DishName ?? x.RestaurantName) is not null))
            lines.Add($"Theo yêu cầu của bạn: {Safe(request.DishName ?? request.RestaurantName)} ở {Meal(request.MealSlot!)} ngày {request.DayNumber}.");
        if (snapshot.State.TasteTargets.Length > 0 || snapshot.State.TasteOverrides.Length > 0)
            lines.Add(TasteNote);
        if (substituted > 0) lines.Add($"Có {substituted} {SubstitutionNote}");
        // Lặp còn lại sau khi backend đã thay các món lặp tránh được; món người dùng gọi tên không tính.
        if (snapshot.Items.Where(item => AgentScopedPlanning.Request(snapshot.State, item.DayNumber, item.MealSlot) is { DishName: null, RestaurantName: null })
                .GroupBy(item => AgentScopedPlanning.Normalize(item.DishName)).Any(group => group.Key is not null && group.Count() > 1))
            lines.Add(RepeatNote);
        lines.Add("Ưng rồi thì cứ bảo mình chốt, muốn đổi món nào cứ nhắn nhé.");
        return string.Join('\n', lines);
    }

    /// <summary>Says exactly what an edit changed and that the rest stayed; a plan with nothing retained reads as a new plan.</summary>
    private static string Summary(AgentSnapshot snapshot, AgentSnapshot? source)
    {
        var total = Money(snapshot.Items.Sum(x => x.Price));
        var sourceItems = source?.Items ?? [];
        var changed = AgentJson.OrderItems(snapshot.Items).Where(item => !sourceItems.Any(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId &&
            old.DayNumber == item.DayNumber && old.MealSlot == item.MealSlot)).ToArray();
        var removed = sourceItems.Where(old => snapshot.Items.All(item => item.ItemKey != old.ItemKey)).ToArray();
        var retained = snapshot.Items.Length - changed.Length;
        if (retained == 0 && removed.Length == 0 || sourceItems.Length == 0)
        {
            var place = string.IsNullOrWhiteSpace(snapshot.State.Province) ? "" : $" ở {Safe(snapshot.State.Province)}";
            return $"Mình đã lên lịch {snapshot.State.NumDays} ngày{place} cho bạn, tổng khoảng {total} đồng/người. Bạn xem danh sách món và các điểm trên bản đồ nhé.";
        }
        var parts = new List<string>();
        if (changed.Length > 0)
        {
            var named = changed.Take(MaxNamedChanges).Select(item => $"{Meal(item.MealSlot)} ngày {item.DayNumber} sang {Safe(item.DishName)}");
            parts.Add($"Mình đổi {string.Join("; ", named)}{(changed.Length > MaxNamedChanges ? $" và {changed.Length - MaxNamedChanges} món khác" : "")}.");
        }
        if (removed.Length > 0)
            parts.Add($"Mình đã bỏ {string.Join(", ", removed.Select(item => Safe(item.DishName)).Distinct())}.");
        if (retained > 0) parts.Add("Các món còn lại giữ nguyên.");
        parts.Add($"Tổng giờ khoảng {total} đồng/người.");
        return string.Join(' ', parts);
    }

    /// <summary>Confirms a completed save with the dates the roadmap now holds, or says the date is still open.</summary>
    public static string Saved(short? numDays, DateOnly? startDate)
    {
        var days = numDays ?? 1;
        if (startDate is not { } start) return $"Mình đã lưu lịch {days} ngày của bạn. Khi chọn được ngày đi, bạn cập nhật sau cũng được nhé.";
        var when = days > 1 ? $"từ {Day(start)} đến {Day(start.AddDays(days - 1))}" : $"vào {Day(start)}";
        return $"Mình đã lưu lịch {days} ngày của bạn, {when}. Muốn chỉnh gì cứ nhắn mình nhé.";
    }

    private static string Day(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Asks about an edit using what is already known: the chosen dish, the named meal or day and what it holds now.</summary>
    private static string EditScope(AgentContext context)
    {
        var items = context.Source?.Items ?? [];
        var names = (IEnumerable<AgentItem> found) => string.Join(", ", found.Select(item => Safe(item.DishName)).Distinct().Take(MaxNamedChanges));
        if (items.FirstOrDefault(item => item.ItemKey == context.Request.SelectedItemKey) is { } selected)
            return $"Bạn muốn đổi {Safe(selected.DishName)} thế nào: sang món khác, món rẻ hơn hay bỏ món này?";
        var references = AgentMealScopePolicy.Read(context.Request.Content, context.State);
        if (references.Meals.FirstOrDefault() is { DayNumber: { } mealDay, MealSlot: { } meal })
        {
            var label = Meal(meal);
            var inMeal = items.Where(item => item.DayNumber == mealDay && item.MealSlot == meal).ToArray();
            return inMeal.Length > 0
                ? $"{char.ToUpperInvariant(label[0])}{label[1..]} ngày {mealDay} đang có {names(inMeal)}. Bạn muốn đổi sang món gì, hay bỏ bữa này?"
                : $"{char.ToUpperInvariant(label[0])}{label[1..]} ngày {mealDay} đang trống. Bạn muốn thêm món gì?";
        }
        if (references.Days.Length > 0)
        {
            var day = references.Days[0];
            var inDay = items.Where(item => item.DayNumber == day).ToArray();
            return inDay.Length > 0 ? $"Ngày {day} đang có {names(inDay)}. Bạn muốn đổi món nào, hay thay cả ngày?" : $"Ngày {day} đang trống. Bạn muốn thêm món gì?";
        }
        var sample = items.FirstOrDefault(item => item.DishName is not null) is { } first ? $" hoặc “bỏ món {Safe(first.DishName)}”" : "";
        return $"Bạn muốn đổi món nào, hay cả lịch? Ví dụ “đổi bữa trưa ngày 1”{sample}.";
    }

    /// <summary>Lists a saved itinerary by day and meal in chat, for a user who asked to see it while no proposal card is on screen.</summary>
    public static string Itinerary(AgentSnapshot snapshot)
    {
        var lines = new List<string> { $"Đây là lịch {snapshot.State.NumDays} ngày đã lưu của bạn nhé.", "" };
        var itemsByMeal = AgentJson.OrderItems(snapshot.Items).ToLookup(item => (item.DayNumber, item.MealSlot));
        for (short day = 1; day <= snapshot.State.NumDays; day++)
        {
            lines.Add($"Ngày {day}:\n");
            foreach (var meal in DeclaredMeals(snapshot, itemsByMeal, day))
            {
                var label = Meal(meal);
                lines.Add($"{char.ToUpperInvariant(label[0])}{label[1..]}:");
                foreach (var item in itemsByMeal[(day, meal)])
                    lines.Add($"- {Safe(item.DishName)} — {Safe(item.RestaurantName)}, {Money(item.Price)} đồng.");
                if (!itemsByMeal[(day, meal)].Any()) lines.Add("Chưa có món trong bữa này.");
                lines.Add("");
            }
        }
        lines.Add($"Tổng tiền dự kiến: {Money(snapshot.Items.Sum(x => x.Price))} đồng/người.");
        lines.Add("Muốn đổi món hay bữa nào cứ nhắn mình nhé.");
        return string.Join('\n', lines);
    }

    /// <summary>Names the meals of a preview that hold no dish, at most six so a sparse plan does not turn back into a long list.</summary>
    private static string[] EmptyMeals(AgentSnapshot snapshot)
    {
        var itemsByMeal = snapshot.Items.ToLookup(item => (item.DayNumber, item.MealSlot));
        var empty = Enumerable.Range(1, snapshot.State.NumDays ?? 0).Select(day => (short)day)
            .SelectMany(day => DeclaredMeals(snapshot, itemsByMeal, day).Where(meal => !itemsByMeal[(day, meal)].Any()).Select(meal => $"{Meal(meal)} ngày {day}"))
            .ToArray();
        return empty.Length > 6 ? [.. empty.Take(6), $"{empty.Length - 6} bữa khác"] : empty;
    }

    /// <summary>Main meals always count; snack and late-night only when the plan declares or already holds them.</summary>
    private static IEnumerable<string> DeclaredMeals(AgentSnapshot snapshot, ILookup<(short DayNumber, string MealSlot), AgentItem> itemsByMeal, short day) =>
        AgentStateValidator.Meals.Where(meal => meal is not ("snack" or "late_night") || itemsByMeal[(day, meal)].Any() || snapshot.State.MealSlots.Contains(meal));

    /// <summary>Answers a bare save intent with what would be saved and the exact save wording, without saving or changing the plan.</summary>
    public static string SaveConfirmation(DTOs.Agent.AgentProposalDto? proposal) => proposal?.Status switch
    {
        "ready" => $"Mình lưu lịch hiện tại ({proposal.Snapshot.Items.Length} món, tổng {Money(proposal.Snapshot.Items.Sum(x => x.Price))} đồng/người) nhé?",
        "committed" => "Lịch này đã được lưu rồi. Bạn muốn chỉnh gì thêm không?",
        _ => NothingToSave
    };

    /// <summary>Names the required trip values still unknown, or null when none is missing; province is optional for wording-only checks.</summary>
    public static string? TripDetailsQuestion(AgentState state, bool askProvince = true)
    {
        // Địa điểm được hỏi riêng và trước tiên: số ngày và ngân sách chỉ có nghĩa khi đã biết sẽ ăn ở đâu.
        if (askProvince && string.IsNullOrWhiteSpace(state.Province)) return AgentIntakePolicy.PlaceQuestion;
        var missing = new List<string>();
        if (!state.NumDays.HasValue) missing.Add("số ngày");
        if (state.BudgetAmount is null && !state.BudgetUnrestricted) missing.Add("ngân sách cho một người (tổng chuyến hoặc mỗi ngày)");
        return missing.Count == 0 ? null : $"Bạn cho mình biết {string.Join(", ", missing)} để lập lịch nhé?";
    }

    /// <summary>Asks only unresolved required values or the model's allowlisted clarification intent.</summary>
    public static string Clarify(AgentContext context, string? kind)
    {
        if (context.ToolNotice is { } notice) return notice;
        if (TripDetailsQuestion(context.State) is { } tripQuestion) return tripQuestion;
        // Gọi đúng tên vị người dùng vừa nhắc và dùng chung thang chữ với câu hỏi khẩu vị cố định của backend.
        var tastes = string.Join(", ", AgentTasteClarificationPolicy.MentionedTastes(context.Request.Content));
        return kind switch
        {
            "taste_scope" => $"Bạn muốn áp dụng khẩu vị {(tastes.Length > 0 ? tastes : "này")} cho toàn chuyến hay ngày/bữa cụ thể nào? Bạn muốn cập nhật các bữa đã có hay chỉ các gợi ý tiếp theo?",
            "taste_intensity" => tastes.Length > 0 ? $"Bạn muốn mức {tastes} như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?"
                : "Bạn muốn vị nào và ở mức nào: không, nhẹ, vừa, nhiều hay rất nhiều?",
            "edit_scope" => EditScope(context),
            "novelty" => NoveltyQuestion,
            // Câu hỏi về catalog dùng kết quả tra cứu thật (tên không tìm thấy, các lựa chọn gần đúng) khi có.
            "catalog_choice" => context.CatalogNotice ?? "Bạn muốn chọn món hoặc quán nào trong các kết quả mình vừa tìm được?",
            "coverage" => Coverage(context),
            "catalog_support" => context.CatalogNotice ?? "Mình chưa có đủ dữ liệu trên ANGI để xác nhận yêu cầu này. Bạn có muốn chọn tiêu chí khác không?",
            "budget" => "Ngân sách hiện tại có thể chưa đủ cho các món phù hợp. Bạn muốn tăng ngân sách, bớt số ngày/bữa, hay đặt mức giá tối đa mỗi bữa để mình chọn món rẻ hơn?",
            // Chưa có lịch nào: không mời "xem lại đề xuất". Đã lấy gợi ý mà chưa ghép được lịch thì nói rõ là chưa hoàn tất.
            _ when context.Source is null => context.Exchanges.Count > 0 ? PlanningIncomplete
                : "Mình đã có địa điểm, số ngày và ngân sách. Bạn muốn mình lập lịch ngay hay bổ sung yêu cầu về bữa, món hoặc khẩu vị?",
            _ => "Bạn muốn xem lại lịch hiện tại hay thay đổi ngày/bữa hoặc món nào?"
        };
    }

    /// <summary>True when the only reason a proposal failed validation is a total above the trip budget.</summary>
    public static bool OverBudgetOnly(AgentValidation? validation) => validation is { Errors.Length: > 0 } &&
        validation.Errors.All(error => error == AgentProposalValidator.BudgetExceeded);

    /// <summary>
    /// Asks how to resolve a budget the authorized dishes did not fit, with the observed figures, instead of a generic failure.
    /// Only the user may raise the budget, drop meals or cap prices; the backend never relaxes the budget itself.
    /// </summary>
    public static string BudgetShortfall(AgentState state, AgentValidation validation)
    {
        var perMeal = AgentScopedPlanning.BudgetPerMeal(state) is { } amount
            ? $" (khoảng {Money(amount)} đồng/bữa cho {AgentScopedPlanning.MealCount(state)} bữa)" : "";
        return $"Các món phù hợp mình tìm được có tổng {Money(validation.TotalCost)} đồng/người, vượt ngân sách {Money(state.BudgetAmount ?? 0)} đồng " +
            $"cho cả chuyến{perMeal}. Bạn muốn tăng ngân sách, bớt số ngày/bữa, hay đặt mức giá tối đa mỗi bữa để mình chọn món rẻ hơn?";
    }

    /// <summary>Only current, backend-observed empty meals support a claim of missing choices.</summary>
    public static AgentMealCoverage[] CoverageGaps(AgentContext context) => context.MealCoverage
        .Where(meal => meal.AvailableCount == 0 && meal.DayNumber <= context.State.NumDays &&
            (context.State.MealSlots.Contains(meal.MealSlot) || context.State.MealSlots.Length == 0) &&
            meal.Fingerprint == AgentScopedPlanning.Fingerprint(context.State, meal.DayNumber, meal.MealSlot))
        .OrderBy(meal => meal.DayNumber).ThenBy(meal => Array.IndexOf(UseCases.Validators.Agent.AgentStateValidator.Meals, meal.MealSlot)).ToArray();

    /// <summary>Names missing day/meals and known filters without claiming the whole catalog is exhausted.</summary>
    public static string Coverage(AgentContext context)
    {
        var gaps = CoverageGaps(context);
        if (gaps.Length == 0) return PlanningIncomplete;
        var details = gaps.Take(6).Select(meal => $"ngày {meal.DayNumber}, {Meal(meal.MealSlot)}" +
            (meal.RequiredTags.Length == 0 ? "" : $" (yêu cầu: {string.Join(", ", meal.RequiredTags.Select(Safe))})"));
        var remaining = gaps.Length > 6 ? $" và {gaps.Length - 6} bữa khác" : "";
        return $"Trong kết quả gợi ý vừa lấy, chưa có lựa chọn phù hợp cho {string.Join("; ", details)}{remaining}. " +
            "Mình chưa hoàn tất lịch mới. Bạn muốn mình tìm thêm cho các bữa này hay thay đổi yêu cầu của các bữa đó?";
    }

    /// <summary>Answers using a trusted tool outcome; the itinerary is shown only when asked for, never as a default reply to any other question.</summary>
    public static string Answer(AgentContext context)
    {
        if ((context.ToolNotice ?? context.CatalogAnswer) is { } known) return known;
        if (context.Source is not { } source)
            return "Mình có thể lập lịch ăn uống từ những món và quán có trên ANGI. Bạn muốn lập lịch hoặc tìm món/quán nào?";
        if (!AgentChatPolicy.IsPlanViewRequest(context.Request.Content)) return AnswerUnavailable;
        // Bản nháp đang hiện trên thẻ đề xuất nên chỉ cần trỏ tới đó; lịch đã lưu không có thẻ nào nên liệt kê ngay trong chat.
        return context.CurrentProposal?.Status == "ready"
            ? $"Lịch hiện tại có {source.Items.Length} món, tổng khoảng {Money(source.Items.Sum(x => x.Price))} đồng/người. Bạn xem chi tiết ở thẻ bên dưới và trên bản đồ nhé."
            : Itinerary(source);
    }

    internal static string Meal(string value) => value switch
    { "breakfast" => "bữa sáng", "lunch" => "bữa trưa", "dinner" => "bữa tối", "snack" => "bữa phụ", "late_night" => "bữa khuya", _ => "bữa ăn" };

    /// <summary>Lists each user taste with its level and scope ("chua mức vừa cho bữa trưa ngày 1"), or null when there is none.</summary>
    private static string? TasteScopes(AgentState state)
    {
        var parts = state.TasteTargets.Select(target => $"{Taste(target)} cho toàn chuyến")
            .Concat(state.TasteOverrides.OrderBy(x => x.DayNumber).ThenBy(x => x.MealSlot is null ? -1 : Array.IndexOf(AgentStateValidator.Meals, x.MealSlot))
                .SelectMany(x => x.Targets.Select(target => $"{Taste(target)} cho {(x.MealSlot is null ? "" : Meal(x.MealSlot) + " ")}ngày {x.DayNumber}")))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("; ", parts);
    }

    // Cùng thang chữ với câu hỏi mức vị: không, nhẹ, vừa, nhiều, rất nhiều.
    private static string Taste(AgentTasteTarget target) => (target.Dimension switch
    {
        "sweet" => "ngọt", "sour" => "chua", "salty" => "mặn", "bitter" => "đắng", "umami" => "umami", "spicy" => "cay",
        "cooling" => "thanh mát", "astringent" => "chát", "fatty" => "béo", "fresh" => "tươi", "rich" => "đậm đà", "aromatic" => "thơm",
        _ => target.Dimension
    }) + " mức " + target.Target switch { 0d => "không", .25 => "nhẹ", .5 => "vừa", .75 => "nhiều", _ => "rất nhiều" };
    private static string Money(decimal value) => value.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
    internal static string Safe(string? value) => value is null ? "Chưa có thông tin" : new string(value
        .Where(character => !char.IsControl(character) && character is not '<' and not '>').Take(200).ToArray());
}
