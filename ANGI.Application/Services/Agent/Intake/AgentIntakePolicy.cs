using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Places;

namespace ANGI.Application.Services.Agent.Intake;

/// <summary>
/// Collects a new plan's requirements in a fixed order without a provider call: place, then duration and budget, then optional
/// preferences. Only the user's own words or coordinates decide the place; a roadmap title never does.
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
    public const string LocationNeeded = "Mình chưa lấy được vị trí hiện tại của bạn. Bạn cho phép truy cập vị trí rồi bấm “Dùng vị trí hiện tại”, hoặc nhắn tên tỉnh/thành phố nhé.";
    public const string PreferencesQuestion = "Bạn có khẩu vị riêng hay yêu cầu gì cho từng ngày, từng bữa không? Ví dụ: ít cay, bữa tối ngày 1 ăn hải sản. " +
        "Nếu không có, bạn nhắn “không” để mình lập lịch luôn nhé.";
    // Câu dài không nêu địa điểm/số ngày/ngân sách là người dùng đang hỏi việc khác, không phải trả lời câu hỏi: để model đọc nó.
    private const int MaxAnswerWords = 4;

    private static readonly Regex PlanningWords = new(
        @"\b(?:lap|len|tao|xep|sap xep|lam|thiet ke|dat)\s+(?:(?:giup|dum|ho|cho)\s+(?:toi|minh|em)\s+)?(?:(?:mot|1|cai)\s+)?(?:lich|ke hoach|lo trinh|roadmap|hanh trinh)\b|" +
        @"\b(?:muon|di)\s+(?:trai nghiem|kham pha|du lich)\b|^(?:trai nghiem|kham pha)\b", RegexOptions.CultureInvariant);
    private static readonly Regex HereWords = new(@"\b(?:vi tri (?:hien tai|cua toi|cua minh)|gan day|quanh day|gan toi|gan minh|cho (?:toi|minh) dang o|noi (?:toi|minh) dang o)\b", RegexOptions.CultureInvariant);
    private static readonly Regex PreferenceWords = new(
        @"\b(?:bua (?:sang|trua|toi|phu|khuya)|ngay \d+ (?:an|chi)|mon|quan|an chay|hai san|khong an|di ung|khau vi|an vat|do uong)\b", RegexOptions.CultureInvariant);
    // Trả lời câu hỏi phạm vi là không cần khu cụ thể: "toàn thành phố", "cả tỉnh", "không", "đâu cũng được".
    private static readonly Regex WholeProvinceWords = new(@"\b(?:toan|ca)\s+(?:thanh pho|tinh|tp)\b|^(?:khong|ko|binh thuong|tuy|tuy ban|dau cung duoc|o dau cung duoc|sao cung duoc|mac dinh)(?:\s+(?:nhe|nha|a|ban))*$", RegexOptions.CultureInvariant);
    private static readonly (string Alias, string Name)[] Aliases =
        [("hn", "ha noi"), ("hcm", "ho chi minh"), ("tphcm", "ho chi minh"), ("tp hcm", "ho chi minh"), ("sai gon", "ho chi minh"), ("saigon", "ho chi minh")];

    /// <summary>True only for a chat turn with no plan yet; editing an existing plan or answering a staged change stays with the existing flow.</summary>
    public static bool Eligible(AgentContext context) => context.Request.Command == "chat" && context.State.PendingStateChange is null && !HasPlan(context);

    /// <summary>A plan exists once a ready proposal or a saved item exists; an empty roadmap is still only a trip frame.</summary>
    public static bool HasPlan(AgentContext context) => context.CurrentProposal?.Status == "ready" || context.Source is { Items.Length: > 0 };

    /// <summary>True when the turn supplies coordinates that still need a province.</summary>
    public static bool NeedsNearby(AgentContext context) => context.State.OriginMode == "gps" && context.State.OriginLatitude.HasValue &&
        context.State.OriginLongitude.HasValue && string.IsNullOrWhiteSpace(context.State.Province);

    /// <summary>Reads the current message into a proposed state, or returns null when it neither starts nor continues an intake.</summary>
    public static AgentIntakeRead? Read(AgentContext context, IReadOnlyCollection<string> provinces, string? nearby, AgentTripPlaces? trip = null, bool askScope = false)
    {
        var state = context.State;
        var text = Plain(context.Request.Content);
        // Câu trả lời cho câu hỏi khẩu vị/yêu cầu kết thúc phần hỏi: lượt này lập lịch, nội dung trả lời do model đọc.
        if (state.PlanningIntake == Preferences)
        {
            var planned = trip is null ? state : AgentItineraryPolicy.Apply(state, trip);
            return new(planned, planned, false, false, null);
        }
        var named = FindProvince(text, provinces) ?? trip?.Province;
        var located = NeedsNearby(context);
        var opened = state.PlanningIntake is not null;
        if (!opened && (context.ReplanAfterConfirmation || trip is null && !located && !PlanningWords.IsMatch(text) && (named is null || StatesPreference(text)))) return null;
        var current = (trip is null ? state : AgentItineraryPolicy.Apply(state, trip)) with { PlanningIntake = Details };
        // Có tọa độ nhưng trong bán kính không có quán nào: không gán tỉnh gần đúng, bỏ tọa độ và hỏi lại địa điểm.
        if (located && nearby is null && named is null)
            return new(current, current, false, false, $"Quanh vị trí hiện tại của bạn chưa có quán nào trên ANGI. {Coverage(provinces)} {PlaceQuestion}");
        if (string.IsNullOrWhiteSpace(state.Province) && named is null && !located && HereWords.IsMatch(text))
            return new(current, current, false, false, LocationNeeded);
        var budgetKnown = state.BudgetAmount.HasValue || state.BudgetUnrestricted;
        var request = AgentPendingPlanningPolicy.CaptureAnswer(state.PendingPlanningRequest, context.Request.Content, !budgetKnown && state.NumDays.HasValue);
        var province = named ?? (located ? nearby : state.Province);
        var proposed = AgentPendingPlanningPolicy.Apply(current with { Province = province, RadiusKm = located ? DefaultRadiusKm : current.RadiusKm }, request);
        if (!budgetKnown && AgentPendingPlanningPolicy.StatesUnrestricted(context.Request.Content))
            proposed = proposed with { BudgetUnrestricted = true };
        var answeredScope = state.PlanningIntake == Scope;
        var facts = proposed.NumDays != state.NumDays || proposed.BudgetAmount != state.BudgetAmount ||
            proposed.BudgetUnrestricted != state.BudgetUnrestricted || request.DailyBudget != state.PendingPlanningRequest?.DailyBudget;
        var whole = answeredScope && StatesWholeProvince(context.Request.Content, province);
        var progress = trip is not null || province != state.Province || facts || whole || answeredScope && current.OriginLatitude.HasValue;
        var note = trip is not null ? AgentItineraryPolicy.Note(proposed, trip) : answeredScope ? ScopeNote(proposed, whole || facts) : null;
        // Chỉ nói tên tỉnh, chưa rõ phạm vi: hỏi một lần. Đã có khu, hành trình hoặc tọa độ thì không có gì để hỏi;
        // tin nhắn đã nêu số ngày, ngân sách hay khẩu vị vẫn đi theo luồng cũ, chọn trong toàn tỉnh.
        var needsScope = askScope && province != state.Province && !string.IsNullOrWhiteSpace(province) && trip is null && !located &&
            !current.OriginLatitude.HasValue && current.DayAreas is null && !facts && !StatesPreference(context.Request.Content);
        return new(current with { PendingPlanningRequest = request }, proposed with { PendingPlanningRequest = request }, progress, province != state.Province, null, note, needsScope);
    }

    /// <summary>Confirms how the scope question was answered; a reply it could not place keeps the whole province, so the user is never stuck on this question.</summary>
    private static string ScopeNote(AgentState state, bool understood)
    {
        var province = AgentReplyBuilder.Safe(state.Province);
        if (state.OriginLatitude.HasValue && state.RadiusKm is { } radius) return $"Mình sẽ ưu tiên quán trong khoảng {radius:0} km quanh vị trí của bạn.";
        return understood ? $"Mình sẽ chọn quán trong toàn {province}."
            : $"Mình chưa nhận ra khu này nên sẽ chọn quán trong toàn {province}; muốn đổi thì bạn nhắn “ở” kèm tên khu nhé.";
    }

    private static string ScopeQuestion(string province) =>
        $"Bạn muốn mình chọn quán trong toàn {AgentReplyBuilder.Safe(province)}, quanh một khu như Cầu Giấy, theo hành trình từng ngày như “Ngày 1 ở Hoàn Kiếm, ngày 2 đi Lăng Bác”, hay quanh vị trí hiện tại?";

    /// <summary>True when the reply asks for the whole province rather than a place inside it.</summary>
    internal static bool StatesWholeProvince(string message, string? province)
    {
        var text = Plain(message);
        return WholeProvinceWords.IsMatch(text) || province is not null && Regex.IsMatch(text, $@"\b(?:toan|ca)\s+{Regex.Escape(Plain(province))}\b", RegexOptions.CultureInvariant);
    }

    /// <summary>True when the message refers to the user's own position, which only coordinates can answer.</summary>
    internal static bool StatesHere(string message) => HereWords.IsMatch(Plain(message));

    /// <summary>Decides the turn after the proposed state was accepted: the next question, or closing the intake.</summary>
    public static AgentIntakeStep Next(AgentContext context, AgentState accepted, AgentIntakeRead read, IReadOnlyCollection<string> provinces)
    {
        var opened = context.State.PlanningIntake is not null;
        if (context.State.PlanningIntake == Preferences) return Close(accepted, true);
        var request = accepted.PendingPlanningRequest ?? new([context.Request.Content.Trim()]);
        var longAside = opened && !read.Progress && context.Request.ConfirmedState is null && WordCount(context.Request.Content) > MaxAnswerWords;
        if (string.IsNullOrWhiteSpace(accepted.Province))
        {
            if (longAside) return Close(accepted with { PendingPlanningRequest = null }, false);
            // Người dùng đã nêu nơi đến nhưng không có quán quanh đó: nói rõ và cho biết ANGI có dữ liệu ở đâu, thay vì hỏi lại như chưa nghe gì.
            if ((read.Note ?? context.PinNote) is { } unknown) return Ask(accepted, Details, $"{unknown} {Coverage(provinces)} {PlaceQuestion}");
            return Ask(accepted, Details, opened && !read.Progress ? $"Mình chưa nhận ra địa điểm trong câu của bạn. {Coverage(provinces)} {PlaceQuestion}" : PlaceQuestion);
        }
        var acknowledgement = (read.Note ?? context.PinNote) is { } note ? note + " " : read.PlaceChanged ? Acknowledge(accepted, context) + " " : "";
        if (read.NeedsScope) return Ask(accepted, Scope, acknowledgement + ScopeQuestion(accepted.Province!));
        if (AgentPendingPlanningPolicy.MissingTripDetails(accepted, request) is { } question)
        {
            if (longAside) return Close(accepted with { PendingPlanningRequest = null }, false);
            return Ask(accepted, Details, acknowledgement + question);
        }
        return request.Messages.Any(StatesPreference) ? Close(accepted, true) : Ask(accepted, Preferences, acknowledgement + PreferencesQuestion);
    }

    /// <summary>A province the model can only have read from the roadmap title is dropped, so "3 ngày ăn ngon Hà Nội" cannot answer the place question.</summary>
    public static AgentState? WithoutUnstatedProvince(AgentState? proposed, AgentContext context)
    {
        if (proposed?.Province is not { } province || !string.IsNullOrWhiteSpace(context.State.Province) || HasPlan(context) ||
            context.State.OriginLatitude.HasValue || FindProvince(Plain(context.State.Title), [province]) is null) return proposed;
        var messages = context.History.Where(turn => turn.Role == "user").Select(turn => turn.Content).Append(context.Request.Content).Select(Plain);
        return messages.Any(message => FindProvince(message, [province]) is not null) ? proposed : proposed with { Province = null };
    }

    /// <summary>Matches a catalog province by whole words without accents, including common short names.</summary>
    public static string? FindProvince(string plainText, IEnumerable<string> provinces)
    {
        var text = $" {plainText} ";
        foreach (var province in provinces)
        {
            var name = Plain(province);
            if (name.Length > 0 && (text.Contains($" {name} ", StringComparison.Ordinal) ||
                Aliases.Any(alias => $" {name} ".Contains($" {alias.Name} ", StringComparison.Ordinal) && text.Contains($" {alias.Alias} ", StringComparison.Ordinal))))
                return province;
        }
        return null;
    }

    private static AgentIntakeStep Ask(AgentState state, string stage, string question) =>
        new(question, state with { PlanningIntake = stage, PendingQuestion = question[..Math.Min(question.Length, 1000)] }, false, false);

    private static AgentIntakeStep Close(AgentState state, bool plan) =>
        new(null, state with { PlanningIntake = null, PendingQuestion = null }, true, plan);

    private static string Acknowledge(AgentState state, AgentContext context) => NeedsNearby(context)
        ? $"Bạn đang ở gần {AgentReplyBuilder.Safe(state.Province)}, mình sẽ ưu tiên quán trong khoảng {state.RadiusKm:0} km quanh bạn."
        : $"Mình ghi nhận {AgentReplyBuilder.Safe(state.Province)}.";

    private static string Coverage(IReadOnlyCollection<string> provinces) => provinces.Count == 0 ? "Hiện ANGI chưa có dữ liệu quán ăn."
        : $"Hiện ANGI có dữ liệu ở {string.Join(", ", provinces.Take(10).Select(AgentReplyBuilder.Safe))}.";

    private static bool StatesPreference(string message) =>
        AgentTasteClarificationPolicy.StatesTaste(message) || StatesFoodRequest(message);

    // "tham quan" và "quận" bỏ dấu đều có chữ "quan", dễ nhầm với "quán": hành trình "tôi đi tham quan" từng làm mất câu hỏi khẩu vị.
    private static bool StatesFoodRequest(string message) =>
        PreferenceWords.IsMatch(Plain(Regex.Replace(message.Normalize(NormalizationForm.FormC), "tham quan|quận", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));

    /// <summary>True when the message asks for a new plan, in the wording that opens the pre-plan questions.</summary>
    internal static bool StatesPlan(string message) => PlanningWords.IsMatch(Plain(message));

    private static int WordCount(string message) => Plain(message).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    internal static string Plain(string text)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        return Regex.Replace(Regex.Replace(plain, @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
    }
}

/// <summary>What the current message states, before state policy accepts it; a reply means the turn only asks again.</summary>
public sealed record AgentIntakeRead(AgentState Current, AgentState Proposed, bool Progress, bool PlaceChanged, string? Reply, string? Note = null, bool NeedsScope = false);

/// <summary>A question to ask, or a closed intake whose message continues through the normal planning flow.</summary>
public sealed record AgentIntakeStep(string? Reply, AgentState State, bool Closed, bool Plan);
