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

    private static readonly (string Alias, string Name)[] Aliases =
        [("hn", "ha noi"), ("hcm", "ho chi minh"), ("tphcm", "ho chi minh"), ("tp hcm", "ho chi minh"), ("sai gon", "ho chi minh"), ("saigon", "ho chi minh")];

    /// <summary>True only for a chat turn with no plan yet; editing an existing plan or answering a staged change stays with the existing flow.</summary>
    public static bool Eligible(AgentContext context) => context.Request.Command == "chat" && context.State.PendingStateChange is null && !HasPlan(context);

    /// <summary>A plan exists once a ready proposal or a saved item exists; an empty roadmap is still only a trip frame.</summary>
    /// <summary>
    /// True once the user is planning in this conversation: a plan exists, a planning question was asked, or a planning request is being
    /// clarified. Trip values loaded from the roadmap the chat was opened on do not count, so a bare taste there is still a dish idea.
    /// </summary>
    public static bool PlanningStarted(AgentContext context) =>
        HasPlan(context) || context.State.PlanningIntake is not null || context.State.PendingPlanningRequest is not null;

    public static bool HasPlan(AgentContext context) => context.CurrentProposal?.Status == "ready" || context.Source is { Items.Length: > 0 };

    /// <summary>True when the turn supplies coordinates that still need a province.</summary>
    public static bool NeedsNearby(AgentContext context) => context.State.OriginMode == "gps" && context.State.OriginLatitude.HasValue &&
        context.State.OriginLongitude.HasValue && string.IsNullOrWhiteSpace(context.State.Province);

    /// <summary>
    /// The state constraint changes are judged against while no plan exists: nothing can be lost yet, so stating or correcting the place and duration, or leaving an unset budget open,
    /// needs no second confirmation. A budget already set and the rest of the state keep their normal protection, because the model must not drop a limit the user gave.
    /// </summary>
    public static AgentState Baseline(AgentState state) => state with { Province = null, NumDays = null, BudgetUnrestricted = state.BudgetUnrestricted || state.BudgetAmount is null };

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

    /// <summary>The one catalog province a roadmap title names; null when it names none or several.</summary>
    public static string? ProvinceInTitle(string title, IEnumerable<string> provinces)
    {
        var plain = Plain(title);
        var named = provinces.Where(province => FindProvince(plain, [province]) is not null).Take(2).ToArray();
        return named.Length == 1 ? named[0] : null;
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
