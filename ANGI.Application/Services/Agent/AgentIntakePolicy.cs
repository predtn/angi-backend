using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>
/// Collects a new plan's requirements in a fixed order without a provider call: place, then duration and budget, then optional
/// preferences. Only the user's own words or coordinates decide the place; a roadmap title never does.
/// </summary>
public static class AgentIntakePolicy
{
    public const string Details = "details";
    public const string Preferences = "preferences";
    /// <summary>Nearest-restaurant search limit; matches the largest radius the state validator accepts.</summary>
    public const decimal MaxNearbyKm = 50m;
    public const decimal DefaultRadiusKm = 10m;
    public const string PlaceQuestion = "Bạn muốn trải nghiệm ẩm thực ở đâu? Bạn nhắn tên tỉnh/thành phố hoặc dùng vị trí hiện tại nhé.";
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
    public static AgentIntakeRead? Read(AgentContext context, IReadOnlyCollection<string> provinces, string? nearby)
    {
        var state = context.State;
        var text = Plain(context.Request.Content);
        // Câu trả lời cho câu hỏi khẩu vị/yêu cầu kết thúc phần hỏi: lượt này lập lịch, nội dung trả lời do model đọc.
        if (state.PlanningIntake == Preferences) return new(state, state, false, false, null);
        var named = FindProvince(text, provinces);
        var located = NeedsNearby(context);
        var opened = state.PlanningIntake is not null;
        if (!opened && (context.ReplanAfterConfirmation || !located && !PlanningWords.IsMatch(text) && (named is null || StatesPreference(text)))) return null;
        var current = state with { PlanningIntake = Details };
        // Có tọa độ nhưng trong bán kính không có quán nào: không gán tỉnh gần đúng, bỏ tọa độ và hỏi lại địa điểm.
        if (located && nearby is null && named is null)
            return new(current, current, false, false, $"Quanh vị trí hiện tại của bạn chưa có quán nào trên ANGI. {Coverage(provinces)} {PlaceQuestion}");
        if (string.IsNullOrWhiteSpace(state.Province) && named is null && !located && HereWords.IsMatch(text))
            return new(current, current, false, false, LocationNeeded);
        var budgetKnown = state.BudgetAmount.HasValue || state.BudgetUnrestricted;
        var request = AgentPendingPlanningPolicy.CaptureAnswer(state.PendingPlanningRequest, context.Request.Content, !budgetKnown && state.NumDays.HasValue);
        var province = named ?? (located ? nearby : state.Province);
        var proposed = AgentPendingPlanningPolicy.Apply(current with { Province = province, RadiusKm = located ? DefaultRadiusKm : state.RadiusKm }, request);
        if (!budgetKnown && AgentPendingPlanningPolicy.StatesUnrestricted(context.Request.Content))
            proposed = proposed with { BudgetUnrestricted = true };
        var progress = province != state.Province || proposed.NumDays != state.NumDays || proposed.BudgetAmount != state.BudgetAmount ||
            proposed.BudgetUnrestricted != state.BudgetUnrestricted || request.DailyBudget != state.PendingPlanningRequest?.DailyBudget;
        return new(current with { PendingPlanningRequest = request }, proposed with { PendingPlanningRequest = request }, progress, province != state.Province, null);
    }

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
            return Ask(accepted, Details, opened && !read.Progress ? $"Mình chưa nhận ra địa điểm trong câu của bạn. {Coverage(provinces)} {PlaceQuestion}" : PlaceQuestion);
        }
        var acknowledgement = read.PlaceChanged ? Acknowledge(accepted, context) + " " : "";
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
        AgentTasteClarificationPolicy.StatesTaste(message) || PreferenceWords.IsMatch(Plain(message));

    private static int WordCount(string message) => Plain(message).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Plain(string text)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        return Regex.Replace(Regex.Replace(plain, @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
    }
}

/// <summary>What the current message states, before state policy accepts it; a reply means the turn only asks again.</summary>
public sealed record AgentIntakeRead(AgentState Current, AgentState Proposed, bool Progress, bool PlaceChanged, string? Reply);

/// <summary>A question to ask, or a closed intake whose message continues through the normal planning flow.</summary>
public sealed record AgentIntakeStep(string? Reply, AgentState State, bool Closed, bool Plan);
