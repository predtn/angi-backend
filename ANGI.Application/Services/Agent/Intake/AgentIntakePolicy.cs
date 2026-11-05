using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;

namespace ANGI.Application.Services.Agent.Intake;

/// <summary>
/// Guards the order of the questions before a new plan: place, then duration and budget, then optional preferences. The model reads the answers and words the questions;
/// this class only checks state, so a question cannot be skipped however the model read a message. Only the user's own words or coordinates decide the place; a roadmap title never does.
/// </summary>
public static class AgentIntakePolicy
{
    public const string Details = "details";
    public const string Preferences = "preferences";
    /// <summary>Stage after a bare province: the whole province, an area, an itinerary or the current location.</summary>
    public const string Scope = "scope";
    /// <summary>Nearest-restaurant search limit; matches the largest radius the state validator accepts.</summary>
    public const decimal MaxNearbyKm = 50m;
    public const decimal DefaultRadiusKm = 10m;
    public const string PlaceQuestion = "Bạn muốn trải nghiệm ẩm thực ở đâu? Bạn có thể nhắn tên tỉnh/thành phố để mình chọn quán trong toàn thành phố, một khu như Cầu Giấy, " +
        "hành trình từng ngày như “Ngày 1 ở Hoàn Kiếm, ngày 2 đi Lăng Bác”, hoặc dùng vị trí hiện tại nhé.";
    public const string PreferencesQuestion = "Bạn có khẩu vị riêng hay yêu cầu gì cho từng ngày, từng bữa không? Ví dụ: ít cay, bữa tối ngày 1 ăn hải sản. " +
        "Nếu không có, bạn nhắn “không” để mình lập lịch luôn nhé.";

    private static readonly Regex PreferenceWords = new(
        @"\b(?:bua (?:sang|trua|toi|phu|khuya)|ngay \d+ (?:an|chi)|mon|quan|an chay|hai san|khong an|di ung|khau vi|an vat|do uong)\b", RegexOptions.CultureInvariant);
    // Câu trả lời "không có yêu cầu gì" cho câu hỏi khẩu vị, kể cả nút "Không, lập lịch luôn nhé."; "không ăn cay" không khớp.
    private static readonly Regex Declines = new(@"^(?:khong|ko|thoi|bo qua|khong co|khong can|khong co gi)(?:\s+(?:lap lich luon|lap lich|luon|nhe|nha|dau|a|ban))*$", RegexOptions.CultureInvariant);
    private static readonly (string Alias, string Name)[] Aliases =
        [("hn", "ha noi"), ("hcm", "ho chi minh"), ("tphcm", "ho chi minh"), ("tp hcm", "ho chi minh"), ("sai gon", "ho chi minh"), ("saigon", "ho chi minh")];

    /// <summary>True only for a chat turn with no plan yet; editing an existing plan or answering a staged change stays with the existing flow.</summary>
    public static bool Eligible(AgentContext context) => context.Request.Command == "chat" && context.State.PendingStateChange is null && !HasPlan(context);

    /// <summary>A plan exists once a ready proposal or a saved item exists; an empty roadmap is still only a trip frame.</summary>
    public static bool HasPlan(AgentContext context) => context.CurrentProposal?.Status == "ready" || context.Source is { Items.Length: > 0 };

    /// <summary>True when the turn supplies coordinates that still need a province.</summary>
    public static bool NeedsNearby(AgentContext context) => context.State.OriginMode == "gps" && context.State.OriginLatitude.HasValue &&
        context.State.OriginLongitude.HasValue && string.IsNullOrWhiteSpace(context.State.Province);

    /// <summary>
    /// The state constraint changes are judged against while no plan exists: nothing can be lost yet, so stating or correcting the place and duration, or leaving an unset budget open,
    /// needs no second confirmation. A budget already set and the rest of the state keep their normal protection, because the model must not drop a limit the user gave.
    /// </summary>
    public static AgentState Baseline(AgentState state) => state with { Province = null, NumDays = null, BudgetUnrestricted = state.BudgetUnrestricted || state.BudgetAmount is null };

    /// <summary>
    /// True when the trip is settled and the preferences question has not been asked, so a new plan must wait for it. A taste or request already in the state has answered it.
    /// This reads the state only, never the user's wording. A trip submitted through the constraints form goes straight to planning, while the location button only answers where.
    /// </summary>
    public static bool PreferencesDue(AgentContext context) => context.CatalogProvinces is not null && Eligible(context) && !context.ReplanAfterConfirmation && context.State.PlanningIntake != Preferences &&
        context.Request.ConfirmedState?.OriginMode is null or "gps" && AgentReplyBuilder.TripDetailsQuestion(context.State) is null && !StatesPreference(context.State);

    /// <summary>The preferences question as fixed text and the state that records it was asked; says plainly when the plan will have no budget.</summary>
    public static (string Reply, AgentState State) AskPreferences(AgentState state)
    {
        // STEP-01: Prepare question, then return it with the recorded stage.
        var question = PreferencesWording(state);
        return (question, state with { PlanningIntake = Preferences, PendingQuestion = question[..Math.Min(question.Length, 1000)] });
    }

    /// <summary>The fixed wording of the preferences question.</summary>
    public static string PreferencesWording(AgentState state) =>
        (state is { BudgetUnrestricted: true, BudgetAmount: null } ? AgentReplyBuilder.NoBudgetNote + " " : "") + PreferencesQuestion;

    /// <summary>The fixed wording of the question after a bare province.</summary>
    public static string ScopeQuestion(string? province) =>
        $"Bạn muốn mình chọn quán trong toàn {AgentReplyBuilder.Safe(province)}, quanh một khu như Cầu Giấy, theo hành trình từng ngày như “Ngày 1 ở Hoàn Kiếm, ngày 2 đi Lăng Bác”, hay quanh vị trí hiện tại?";

    /// <summary>The reply when the user names a province ANGI has no data for.</summary>
    public static string UnknownPlaceReply(string place, IReadOnlyCollection<string> provinces) =>
        $"Mình chưa có dữ liệu quán ăn ở {AgentReplyBuilder.Safe(place)}. {Coverage(provinces)} {PlaceQuestion}";

    /// <summary>The reply when no restaurant lies near the coordinates the user shared.</summary>
    public static string NoRestaurantNearby(string where, IReadOnlyCollection<string> provinces) => $"{where} {Coverage(provinces)} {PlaceQuestion}";

    /// <summary>
    /// Writes a province the model read in the catalog's spelling; one the catalog does not have is dropped and remembered, so the trip never starts somewhere with no restaurants.
    /// </summary>
    public static AgentState? WithCatalogProvince(AgentState? proposed, AgentContext context)
    {
        // STEP-01: Return proposed early when there is no list to check against or no province to check.
        if (context.CatalogProvinces is not { } provinces || string.IsNullOrWhiteSpace(proposed?.Province)) return proposed;
        // STEP-02: Prepare name, then return the catalog's spelling when it matches, ignoring spaces as in "Hanoi".
        var name = Plain(proposed.Province);
        var known = FindProvince(name, provinces) ?? provinces.FirstOrDefault(province => Plain(province).Replace(" ", "") == name.Replace(" ", ""));
        if (known is not null) return proposed with { Province = known };
        // STEP-03: Remember the unknown place and return the state without it.
        context.UnknownPlace = proposed.Province.Trim();
        return proposed with { Province = null };
    }

    /// <summary>A province the model can only have read from the roadmap title is dropped, so "3 ngày ăn ngon Hà Nội" cannot answer the place question.</summary>
    public static AgentState? WithoutUnstatedProvince(AgentState? proposed, AgentContext context)
    {
        // STEP-01: Return proposed early when proposed.Province is not { } province, context.State.Province has a value, HasPlan(context) holds or 2 more condition(s).
        if (proposed?.Province is not { } province || !string.IsNullOrWhiteSpace(context.State.Province) || HasPlan(context) ||
            context.State.OriginLatitude.HasValue || FindProvince(Plain(context.State.Title), [province]) is null) return proposed;
        // STEP-02: Prepare messages through Select(filtered).Append().Select.
        var messages = context.History.Where(turn => turn.Role == "user").Select(turn => turn.Content).Append(context.Request.Content).Select(Plain);
        // STEP-03: Return one of two results depending on messages.Any matches the filter.
        return messages.Any(message => FindProvince(message, [province]) is not null) ? proposed : proposed with { Province = null };
    }

    /// <summary>Matches a catalog province by whole words without accents, including common short names.</summary>
    public static string? FindProvince(string plainText, IEnumerable<string> provinces)
    {
        // STEP-01: Prepare text.
        var text = $" {plainText} ";
        // STEP-02: For each province in provinces, prepare name through Plain and return province early when name.Length > 0 and more.
        foreach (var province in provinces)
        {
            var name = Plain(province);
            if (name.Length > 0 && (text.Contains($" {name} ", StringComparison.Ordinal) ||
                Aliases.Any(alias => $" {name} ".Contains($" {alias.Name} ", StringComparison.Ordinal) && text.Contains($" {alias.Alias} ", StringComparison.Ordinal))))
                return province;
        }
        // STEP-03: Return null.
        return null;
    }

    private static string Coverage(IReadOnlyCollection<string> provinces) => provinces.Count == 0 ? "Hiện ANGI chưa có dữ liệu quán ăn."
        : $"Hiện ANGI có dữ liệu ở {string.Join(", ", provinces.Take(10).Select(AgentReplyBuilder.Safe))}.";

    /// <summary>True when the state already holds something only a preference could have put there.</summary>
    private static bool StatesPreference(AgentState state) => state.TasteTargets.Length > 0 || state.TasteOverrides.Length > 0 || state.MealRequests.Length > 0 ||
        state.TagIds.Length > 0 || state.ExcludeTagIds.Length > 0 || state.MaxPricePerMeal.HasValue;

    // "tham quan" và "quận" bỏ dấu đều có chữ "quan", dễ nhầm với "quán".
    private static bool StatesFoodRequest(string message) =>
        PreferenceWords.IsMatch(Plain(Regex.Replace(message.Normalize(NormalizationForm.FormC), "tham quan|quận", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));

    /// <summary>
    /// A new plan with no special request, whose one search returned dishes for every declared meal: more candidates add cost but
    /// no choice, so the model assembles the plan next. A side dish or named request keeps the tools open. This only saves calls: reading a message wrongly here decides no outcome.
    /// </summary>
    public static bool PlanCovered(AgentContext context)
    {
        // STEP-01: Prepare state and content.
        var state = context.State;
        // Khẩu vị đã nằm trong vector của lượt tìm nên không cần tìm thêm; yêu cầu về món hay bữa thì có thể cần tìm riêng.
        var content = context.Request.Content;
        // STEP-02: Return false early when context.IntakeComplete is false, state.MealSlots.Length is 0, state.MealRequests.Length > 0 or 2 more condition(s).
        if (!context.IntakeComplete || state.MealSlots.Length == 0 || state.MealRequests.Length > 0 ||
            !Declines.IsMatch(Plain(content)) && AgentTasteClarificationPolicy.MentionedTastes(content).Length == 0 ||
            (state.PendingPlanningRequest?.Messages ?? []).Append(content).Any(StatesFoodRequest)) return false;
        // STEP-03: Return the result of Slots().Where(filtered).All.
        return AgentScopedPlanning.Slots(state, new()).Where(slot => state.MealSlots.Contains(slot.Meal))
            .All(slot => context.MealCoverage.Any(meal => meal.DayNumber == slot.Day && meal.MealSlot == slot.Meal && meal.AvailableCount > 0));
    }

    internal static string Plain(string text)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        return Regex.Replace(Regex.Replace(plain, @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
    }

    // The request being clarified: its messages are kept for the model to read, and no figure is read from them here.

    private const int MaxMessages = 6;

    /// <summary>Adds this message to the request, keeping the first message and the most recent ones.</summary>
    public static AgentPendingPlanningRequest CaptureRequest(AgentPendingPlanningRequest? previous, string content)
    {
        // STEP-01: Prepare messages through previous?.Messages ?? [].Append().ToArray.
        var messages = (previous?.Messages ?? []).Append(content.Trim()).ToArray();
        // STEP-02: Return the new result, cut to the first and the latest messages.
        return new(messages.Length > MaxMessages ? [messages[0], .. messages.TakeLast(MaxMessages - 1)] : messages);
    }
}
