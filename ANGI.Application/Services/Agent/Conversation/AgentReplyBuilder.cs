using System.Globalization;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.Services.Agent.Places;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent.Conversation;

/// <summary>Builds factual responses from backend snapshots: the facts that fill the model's sentences, and the fixed replies used when a sentence fails its check.</summary>
public static class AgentReplyBuilder
{
    // Không mời gửi lại y nguyên: cùng dữ liệu và cùng yêu cầu thường lặp lại đúng kết quả nhưng vẫn tốn lượt gọi AI.
    public const string PlanningIncomplete = "Mình chưa hoàn tất việc sắp xếp lịch theo yêu cầu của bạn. " +
        "Bạn có thể nói rõ hơn hoặc điều chỉnh yêu cầu, ví dụ ngày/bữa, món muốn ăn, ngân sách hoặc khẩu vị.";
    // Chỉ còn lặp khi cả danh sách gợi ý của bữa đó (kể cả món dự phòng) không còn món khác tên phù hợp; không suy ra catalog thiếu món.
    public const string RepeatNote = "Một số món xuất hiện ở nhiều bữa vì danh sách gợi ý phù hợp cho các bữa đó không còn món khác.";
    public const string NoveltyQuestion = "Bạn muốn món khác các món trong lịch hiện tại, hay món bạn chưa từng ăn? ANGI chưa có đủ dữ liệu để xác nhận toàn bộ lịch sử ăn uống của bạn.";
    private const int MaxNamedChanges = 3;
    // Các ô model được đặt trong câu về bản nháp; backend điền sau khi đã kiểm tra xong món.
    private static readonly Regex ProposalSlot = new(@"\{(?<slot>total|numDays|place|changes)\}", RegexOptions.CultureInvariant);
    public const string RatingFallbackNote = "Một số món được xếp theo điểm đánh giá vì mình chưa đủ dữ liệu về khẩu vị của bạn.";
    public const string TasteNote = "nhưng chưa thể bảo đảm từng món đúng mức vị đó";
    public const string NoBudgetNote = "Lịch này chưa áp ngân sách. Khi chốt được mức nào, bạn nhắn mình để lập lại nhé.";
    public const string NothingToSave = "Hiện chưa có lịch nào để lưu. Bạn cho mình biết muốn ăn ở đâu, mấy ngày để mình lập lịch nhé.";
    public const string AnswerUnavailable = "Mình chưa trả lời cụ thể được câu này. Bạn nhắn ‘xem lịch’ để xem lại, hoặc cho mình biết muốn đổi ngày, bữa hay món nào nhé.";
    public static readonly string[] Clarifications = ["trip_details", "place_scope", "preferences", "taste_scope", "taste_intensity", "edit_scope",
        "novelty", "catalog_choice", "coverage", "catalog_support", "budget", "general"];

    /// <summary>
    /// Announces a preview in a few sentences: what changed from <paramref name="source"/> when something was kept, otherwise the new plan.
    /// Its dishes and <see cref="PlanNotes"/> are shown by the proposal card and map, not repeated in chat. The opener is friendly wording that states no fact.
    /// </summary>
    public static string Proposal(AgentSnapshot snapshot, AgentSnapshot? source = null, string? opener = null, string? wording = null) =>
        // Câu của model đã tự mời người dùng xem và chốt; câu cố định chỉ cần khi không dùng được câu đó.
        wording ?? $"{opener ?? "Xong rồi!"} {Summary(snapshot, source)}\nƯng rồi thì cứ bảo mình chốt, muốn đổi món nào cứ nhắn nhé.";

    /// <summary>What the user must know about a plan before saving it, shown on the proposal card so it stays in view without interrupting the conversation.</summary>
    public static string[] PlanNotes(AgentSnapshot snapshot)
    {
        // STEP-01: Prepare notes as List<string>.
        var notes = new List<string>();
        // STEP-02: When EmptyMeals() has the expected shape, call notes.Add.
        // Thẻ đề xuất chỉ liệt kê món đã có, nên bữa còn trống phải được nói rõ.
        if (EmptyMeals(snapshot) is { Length: > 0 } empty) notes.Add($"Còn thiếu món cho {string.Join("; ", empty)}.");
        // STEP-03: When snapshot.State.DayAreas has the expected shape, call notes.Add.
        if (snapshot.State.DayAreas is { Length: > 0 } areas)
            notes.Add($"Theo hành trình của bạn: {string.Join("; ", areas.OrderBy(x => x.DayNumber).Select(AgentPlaceResolver.Describe))}" +
                (areas.Length < snapshot.State.NumDays ? $"; các ngày còn lại chọn trong toàn {Safe(snapshot.State.Province)}." : "."));
        // STEP-04: When snapshot.State has the expected shape, call notes.Add.
        // Điểm chấm trên bản đồ là giới hạn tìm quán: nói rõ để người dùng biết vì sao món đều ở quanh đó.
        if (snapshot.State is { OriginMode: "pinned", NamedAreaLabel: null, RadiusKm: { } pinnedKm, DayAreas: null })
            notes.Add($"Mình chọn quán trong khoảng {pinnedKm:0} km quanh điểm bạn đã chấm trên bản đồ.");
        // STEP-05: Add each widened meal to notes.
        // Bữa bị nới xa hơn phải được nói rõ: người dùng có thể đang mong quán sát nơi họ đến.
        notes.AddRange(AgentPlaceResolver.WidenedNotes(snapshot));
        // STEP-06: When snapshot.State has the expected shape, call notes.Add.
        // Không có mức nào chặn tổng tiền: nói rõ để người dùng không tưởng lịch đã theo một ngân sách.
        if (snapshot.State is { BudgetUnrestricted: true, BudgetAmount: null }) notes.Add(NoBudgetNote);
        // STEP-07: When snapshot.Items.Any matches the filter, call notes.Add.
        if (snapshot.Items.Any(item => !item.RequestId.HasValue)) notes.Add(RatingFallbackNote);
        // STEP-08: When TasteScopes() exists, call notes.Add.
        // Nêu rõ mức và phạm vi đã áp dụng để người dùng thấy ngay nếu hệ thống hiểu sai ngày/bữa.
        if (TasteScopes(snapshot.State) is { } tastes) notes.Add($"Mình ưu tiên khẩu vị {tastes}, {TasteNote}.");
        // STEP-09: Add where each named dish or restaurant was placed to notes.
        // Món/quán người dùng gọi tên được xếp vào bữa nào thì nói rõ, để họ biết có thể yêu cầu đổi sang bữa khác.
        notes.AddRange(snapshot.State.MealRequests.Where(x => x.DayNumber.HasValue && x.MealSlot is not null && (x.DishName ?? x.RestaurantName) is not null)
            .Select(request => $"Theo yêu cầu của bạn: {Safe(request.DishName ?? request.RestaurantName)} ở {Meal(request.MealSlot!)} ngày {request.DayNumber}."));
        // STEP-10: When Where(filtered).GroupBy(filtered).Any matches the filter, call notes.Add.
        // Lặp còn lại sau khi backend đã thay các món lặp tránh được; món người dùng gọi tên không tính.
        if (snapshot.Items.Where(item => AgentScopedPlanning.Request(snapshot.State, item.DayNumber, item.MealSlot) is { DishName: null, RestaurantName: null })
                .GroupBy(item => AgentScopedPlanning.Normalize(item.DishName)).Any(group => group.Key is not null && group.Count() > 1))
            notes.Add(RepeatNote);
        // STEP-11: Return notes.
        return [.. notes];
    }

    /// <summary>
    /// Fills the model's sentence about a preview with the facts of the final, validated plan, and adds the total and an edit's changes when it left them out; null when
    /// its own words add a number, name or dish. The model writes this before the backend's repairs, so only slots filled now can be true.
    /// </summary>
    public static string? WordedSummary(string? reply, AgentSnapshot snapshot, AgentSnapshot? source, AgentContext context)
    {
        // STEP-01: Return null early when WordedSummaryIssue() exists.
        if (WordedSummaryIssue(reply, snapshot, source, context) is not null) return null;
        // STEP-02: Prepare changes and total, then fill each slot.
        var changes = Changes(snapshot, source);
        var total = $"{Money(snapshot.Items.Sum(item => item.Price))} đồng/người";
        // Câu mô tả thay đổi đã mở đầu bằng động từ của nó: model viết "Mình bỏ {changes}" thì thành "bỏ bỏ".
        var template = (reply ?? "").Trim();
        if (changes?.Split(' ')[0] is { Length: > 0 } verb) template = Regex.Replace(template, $@"(?<!\p{{L}}){Regex.Escape(verb)}\s+(?=\{{changes\}})", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var filled = ProposalSlot.Replace(template, match => match.Groups["slot"].Value switch
        {
            "total" => total,
            "numDays" => $"{snapshot.State.NumDays}",
            "place" => Safe(snapshot.State.Province),
            _ => changes!
        });
        if (filled.Length > 0 && template.StartsWith('{')) filled = char.ToUpperInvariant(filled[0]) + filled[1..];
        // STEP-03: Prepare missing, and return the sentence followed by the facts it left out.
        // Giữ lời của model thay vì bỏ cả câu chỉ vì thiếu ô: dữ kiện vẫn đủ, do backend viết nối theo.
        var missing = MissingSlots(reply, snapshot, source);
        if (missing.Length == 0) return filled;
        var ended = filled[^1] is '.' or '!' or '?' or '…' ? filled : filled + ".";
        return string.Join(' ', missing.Select(slot => slot == "changes" ? $"Cụ thể: {changes}." : $"Tổng {(changes is null ? "" : "giờ ")}khoảng {total}.").Prepend(ended));
    }

    /// <summary>The facts a preview must state that the model's sentence has no slot for.</summary>
    public static string[] MissingSlots(string? reply, AgentSnapshot snapshot, AgentSnapshot? source)
    {
        var used = ProposalSlot.Matches((reply ?? "").Trim()).Select(match => match.Groups["slot"].Value).ToHashSet();
        return new[] { Changes(snapshot, source) is null ? null : "changes", "total" }.OfType<string>().Where(slot => !used.Contains(slot)).ToArray();
    }

    /// <summary>Why the model's sentence about a preview cannot be shown, as a short code for the log; null when it can.</summary>
    public static string? WordedSummaryIssue(string? reply, AgentSnapshot snapshot, AgentSnapshot? source, AgentContext context)
    {
        // STEP-01: Prepare template and used through ProposalSlot.Matches.
        var template = (reply ?? "").Trim();
        var used = ProposalSlot.Matches(template).Select(match => match.Groups["slot"].Value).ToHashSet();
        // STEP-02: Return "unknown_slot" when the slots do not fit this plan.
        if (Changes(snapshot, source) is null && used.Contains("changes") || used.Contains("place") && string.IsNullOrWhiteSpace(snapshot.State.Province)) return "unknown_slot";
        // STEP-03: Return the result of AgentReplyGuard.TemplateIssue for the words around the slots.
        return AgentReplyGuard.TemplateIssue(ProposalSlot.Replace(template, " ").Trim(), context);
    }

    /// <summary>What an edit changed in words, or null for a plan with nothing kept from before.</summary>
    private static string? Changes(AgentSnapshot snapshot, AgentSnapshot? source)
    {
        // STEP-01: Prepare sourceItems, changed, removed and retained.
        var sourceItems = source?.Items ?? [];
        var changed = AgentJson.OrderItems(snapshot.Items).Where(item => !sourceItems.Any(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId &&
            old.DayNumber == item.DayNumber && old.MealSlot == item.MealSlot)).ToArray();
        var removed = sourceItems.Where(old => snapshot.Items.All(item => item.ItemKey != old.ItemKey)).ToArray();
        // STEP-02: Return null early when nothing is kept and nothing removed, or there was no plan before.
        if (snapshot.Items.Length == changed.Length && removed.Length == 0 || sourceItems.Length == 0) return null;
        // STEP-03: Prepare parts from the changed and removed items.
        var parts = changed.Take(MaxNamedChanges).Select(item => $"{Meal(item.MealSlot)} ngày {item.DayNumber} sang {Safe(item.DishName)}").ToList();
        if (changed.Length > MaxNamedChanges) parts.Add($"{changed.Length - MaxNamedChanges} món khác");
        if (removed.Length > 0) parts.Add($"bỏ {string.Join(", ", removed.Select(item => Safe(item.DishName)).Distinct())}");
        // STEP-04: Return the parts joined, or a plain sentence when the edit changed no dish.
        return parts.Count > 0 ? string.Join("; ", parts) : "giữ nguyên các món";
    }

    /// <summary>Says exactly what an edit changed and that the rest stayed; a plan with nothing retained reads as a new plan.</summary>
    private static string Summary(AgentSnapshot snapshot, AgentSnapshot? source)
    {
        // STEP-01: Prepare total through Money, sourceItems, changed through OrderItems().Where(filtered).ToArray, removed through sourceItems.Where(filtered).ToArray and 1 more.
        var total = Money(snapshot.Items.Sum(x => x.Price));
        var sourceItems = source?.Items ?? [];
        var changed = AgentJson.OrderItems(snapshot.Items).Where(item => !sourceItems.Any(old => old.ItemKey == item.ItemKey && old.DishId == item.DishId &&
            old.DayNumber == item.DayNumber && old.MealSlot == item.MealSlot)).ToArray();
        var removed = sourceItems.Where(old => snapshot.Items.All(item => item.ItemKey != old.ItemKey)).ToArray();
        var retained = snapshot.Items.Length - changed.Length;
        // STEP-02: Return the formatted text early when (retained is 0 and removed.Length is 0) or sourceItems.Length is 0.
        if (retained == 0 && removed.Length == 0 || sourceItems.Length == 0)
        {
            var place = string.IsNullOrWhiteSpace(snapshot.State.Province) ? "" : $" ở {Safe(snapshot.State.Province)}";
            return $"Mình đã lên lịch {snapshot.State.NumDays} ngày{place} cho bạn, tổng khoảng {total} đồng/người. Bạn xem danh sách món và các điểm trên bản đồ nhé.";
        }
        // STEP-03: Prepare parts as List<string>.
        var parts = new List<string>();
        // STEP-04: When changed.Length > 0, prepare named through changed.Take().Select and call parts.Add.
        if (changed.Length > 0)
        {
            var named = changed.Take(MaxNamedChanges).Select(item => $"{Meal(item.MealSlot)} ngày {item.DayNumber} sang {Safe(item.DishName)}");
            parts.Add($"Mình đổi {string.Join("; ", named)}{(changed.Length > MaxNamedChanges ? $" và {changed.Length - MaxNamedChanges} món khác" : "")}.");
        }
        // STEP-05: When removed.Length > 0, call parts.Add.
        if (removed.Length > 0)
            parts.Add($"Mình đã bỏ {string.Join(", ", removed.Select(item => Safe(item.DishName)).Distinct())}.");
        // STEP-06: When retained > 0, call parts.Add.
        if (retained > 0) parts.Add("Các món còn lại giữ nguyên.");
        // STEP-07: Add the built entries to parts.
        parts.Add($"Tổng giờ khoảng {total} đồng/người.");
        // STEP-08: Return the result of string.Join.
        return string.Join(' ', parts);
    }

    /// <summary>Confirms a completed save with the dates the roadmap now holds, or says the date is still open.</summary>
    public static string Saved(short? numDays, DateOnly? startDate)
    {
        // STEP-01: Prepare days.
        var days = numDays ?? 1;
        // STEP-02: Return the formatted text early when startDate is not { } start.
        if (startDate is not { } start) return $"Mình đã lưu lịch {days} ngày của bạn. Khi chọn được ngày đi, bạn cập nhật sau cũng được nhé.";
        // STEP-03: Prepare when from Day.
        var when = days > 1 ? $"từ {Day(start)} đến {Day(start.AddDays(days - 1))}" : $"vào {Day(start)}";
        // STEP-04: Return the formatted text.
        return $"Mình đã lưu lịch {days} ngày của bạn, {when}. Muốn chỉnh gì cứ nhắn mình nhé.";
    }

    private static string Day(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Asks about an edit using what is already known: the chosen dish, the named meal or day and what it holds now.</summary>
    private static string EditScope(AgentContext context)
    {
        // STEP-01: Prepare items and names.
        var items = context.Source?.Items ?? [];
        var names = (IEnumerable<AgentItem> found) => string.Join(", ", found.Select(item => Safe(item.DishName)).Distinct().Take(MaxNamedChanges));
        // STEP-02: Return the formatted text early when items.FirstOrDefault(filtered) exists.
        if (items.FirstOrDefault(item => item.ItemKey == context.Request.SelectedItemKey) is { } selected)
            return $"Bạn muốn đổi {Safe(selected.DishName)} thế nào: sang món khác, món rẻ hơn hay bỏ món này?";
        // STEP-03: Prepare references through AgentMealScopePolicy.Read.
        var references = AgentMealScopePolicy.Read(context.Request.Content, context.State);
        // STEP-04: When references.Meals.FirstOrDefault() has the expected shape, prepare label through Meal, prepare inMeal through items.Where(filtered).ToArray and 1 more action(s).
        if (references.Meals.FirstOrDefault() is { DayNumber: { } mealDay, MealSlot: { } meal })
        {
            var label = Meal(meal);
            var inMeal = items.Where(item => item.DayNumber == mealDay && item.MealSlot == meal).ToArray();
            return inMeal.Length > 0
                ? $"{char.ToUpperInvariant(label[0])}{label[1..]} ngày {mealDay} đang có {names(inMeal)}. Bạn muốn đổi sang món gì, hay bỏ bữa này?"
                : $"{char.ToUpperInvariant(label[0])}{label[1..]} ngày {mealDay} đang trống. Bạn muốn thêm món gì?";
        }
        // STEP-05: When references.Days.Length > 0, prepare day, prepare inDay through items.Where(filtered).ToArray and 1 more action(s).
        if (references.Days.Length > 0)
        {
            var day = references.Days[0];
            var inDay = items.Where(item => item.DayNumber == day).ToArray();
            return inDay.Length > 0 ? $"Ngày {day} đang có {names(inDay)}. Bạn muốn đổi món nào, hay thay cả ngày?" : $"Ngày {day} đang trống. Bạn muốn thêm món gì?";
        }
        // STEP-06: Prepare sample from Safe.
        var sample = items.FirstOrDefault(item => item.DishName is not null) is { } first ? $" hoặc “bỏ món {Safe(first.DishName)}”" : "";
        // STEP-07: Return the formatted text.
        return $"Bạn muốn đổi món nào, hay cả lịch? Ví dụ “đổi bữa trưa ngày 1”{sample}.";
    }

    /// <summary>Lists a saved itinerary by day and meal in chat, for a user who asked to see it while no proposal card is on screen.</summary>
    public static string Itinerary(AgentSnapshot snapshot)
    {
        // STEP-01: Prepare lines as List<string> and itemsByMeal through AgentJson.OrderItems().ToLookup.
        var lines = new List<string> { $"Đây là lịch {snapshot.State.NumDays} ngày đã lưu của bạn nhé.", "" };
        var itemsByMeal = AgentJson.OrderItems(snapshot.Items).ToLookup(item => (item.DayNumber, item.MealSlot));
        // STEP-02: While day <= snapshot.State.NumDays, call lines.Add and go through each meal in DeclaredMeals().
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
        // STEP-03: Add the built entries to lines and add a message to lines.
        lines.Add($"Tổng tiền dự kiến: {Money(snapshot.Items.Sum(x => x.Price))} đồng/người.");
        lines.Add("Muốn đổi món hay bữa nào cứ nhắn mình nhé.");
        // STEP-04: Return the result of string.Join.
        return string.Join('\n', lines);
    }

    /// <summary>Names the meals of a preview that hold no dish, at most six so a sparse plan does not turn back into a long list.</summary>
    private static string[] EmptyMeals(AgentSnapshot snapshot)
    {
        // STEP-01: Prepare itemsByMeal through snapshot.Items.ToLookup and empty through SelectMany(filtered).ToArray.
        var itemsByMeal = snapshot.Items.ToLookup(item => (item.DayNumber, item.MealSlot));
        var empty = Enumerable.Range(1, snapshot.State.NumDays ?? 0).Select(day => (short)day)
            .SelectMany(day => DeclaredMeals(snapshot, itemsByMeal, day).Where(meal => !itemsByMeal[(day, meal)].Any()).Select(meal => $"{Meal(meal)} ngày {day}"))
            .ToArray();
        // STEP-02: Return one of two results depending on empty.Length > 6.
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

    /// <summary>Names the required trip values still unknown, or null when none is missing.</summary>
    public static string? TripDetailsQuestion(AgentState state)
    {
        // STEP-01: Return AgentIntakePolicy.PlaceQuestion early when state.Province is blank.
        // Địa điểm được hỏi riêng và trước tiên: số ngày và ngân sách chỉ có nghĩa khi đã biết sẽ ăn ở đâu.
        if (string.IsNullOrWhiteSpace(state.Province)) return AgentIntakePolicy.PlaceQuestion;
        // STEP-02: Prepare missing as List<string>.
        var missing = new List<string>();
        // STEP-03: When state.NumDays has no value, call missing.Add.
        if (!state.NumDays.HasValue) missing.Add("số ngày");
        // STEP-04: When state.BudgetAmount is missing and state.BudgetUnrestricted is false, call missing.Add.
        if (state.BudgetAmount is null && !state.BudgetUnrestricted) missing.Add("ngân sách cho một người (tổng chuyến hoặc mỗi ngày)");
        // STEP-05: Return one of two results depending on missing.Count is 0.
        return missing.Count == 0 ? null : $"Bạn cho mình biết {string.Join(", ", missing)} để lập lịch nhé?";
    }

    /// <summary>Asks only unresolved required values or the model's allowlisted clarification intent.</summary>
    public static string Clarify(AgentContext context, string? kind)
    {
        // STEP-01: Return notice early when context.ToolNotice exists.
        if (context.ToolNotice is { } notice) return notice;
        // STEP-02: Return tripQuestion early when TripDetailsQuestion() exists.
        if (TripDetailsQuestion(context.State) is { } tripQuestion) return tripQuestion;
        // STEP-03: Prepare tastes through string.Join.
        // Gọi đúng tên vị người dùng vừa nhắc và dùng chung thang chữ với câu hỏi khẩu vị cố định của backend.
        var tastes = string.Join(", ", AgentTasteClarificationPolicy.MentionedTastes(context.Request.Content));
        // STEP-04: Return the value selected by kind.
        return kind switch
        {
            "place_scope" => AgentIntakePolicy.ScopeQuestion(context.State.Province),
            "preferences" => AgentIntakePolicy.PreferencesWording(context.State),
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
        // STEP-01: Prepare gaps through CoverageGaps.
        var gaps = CoverageGaps(context);
        // STEP-02: Return PlanningIncomplete early when gaps.Length is 0.
        if (gaps.Length == 0) return PlanningIncomplete;
        // STEP-03: Prepare details through gaps.Take().Select and remaining.
        var details = gaps.Take(6).Select(meal => $"ngày {meal.DayNumber}, {Meal(meal.MealSlot)}" +
            (meal.RequiredTags.Length == 0 ? "" : $" (yêu cầu: {string.Join(", ", meal.RequiredTags.Select(Safe))})"));
        var remaining = gaps.Length > 6 ? $" và {gaps.Length - 6} bữa khác" : "";
        // STEP-04: Return the computed condition.
        return $"Trong kết quả gợi ý vừa lấy, chưa có lựa chọn phù hợp cho {string.Join("; ", details)}{remaining}. " +
            "Mình chưa hoàn tất lịch mới. Bạn muốn mình tìm thêm cho các bữa này hay thay đổi yêu cầu của các bữa đó?";
    }

    /// <summary>Answers using a trusted tool outcome; the itinerary is shown only when asked for, never as a default reply to any other question.</summary>
    public static string Answer(AgentContext context)
    {
        // STEP-01: Return known early when context.ToolNotice ?? context.CatalogAnswer exists.
        if ((context.ToolNotice ?? context.CatalogAnswer) is { } known) return known;
        // STEP-02: Return a fixed message early when context.Source is not { } source.
        if (context.Source is not { } source)
            return "Mình có thể lập lịch ăn uống từ những món và quán có trên ANGI. Bạn muốn lập lịch hoặc tìm món/quán nào?";
        // STEP-03: Return AnswerUnavailable early when AgentConfirmationPolicy.IsPlanViewRequest(context.Request.Content) does not hold.
        if (!AgentConfirmationPolicy.IsPlanViewRequest(context.Request.Content)) return AnswerUnavailable;
        // STEP-04: Return one of two results depending on context.CurrentProposal.Status is "ready".
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
    private static string Taste(AgentTasteTarget target) => TasteName(target.Dimension) + " mức " + target.Target switch { 0d => "không", .25 => "nhẹ", .5 => "vừa", .75 => "nhiều", _ => "rất nhiều" };

    /// <summary>A taste dimension in the words a message would use for it.</summary>
    internal static string TasteName(string dimension) => dimension switch
    {
        "sweet" => "ngọt", "sour" => "chua", "salty" => "mặn", "bitter" => "đắng", "umami" => "umami", "spicy" => "cay",
        "cooling" => "thanh mát", "astringent" => "chát", "fatty" => "béo", "fresh" => "tươi", "rich" => "đậm đà", "aromatic" => "thơm",
        _ => dimension
    };
    internal static string Money(decimal value) => value.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
    internal static string Safe(string? value) => value is null ? "Chưa có thông tin" : new string(value
        .Where(character => !char.IsControl(character) && character is not '<' and not '>').Take(200).ToArray());
}
