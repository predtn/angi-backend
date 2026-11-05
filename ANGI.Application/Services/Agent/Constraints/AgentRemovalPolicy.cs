using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;

namespace ANGI.Application.Services.Agent.Constraints;

/// <summary>
/// Resolves an unambiguous removal ("xóa bữa tối ngày 2", "bỏ món Phở Gà", a removal of the item selected in the UI) without the model,
/// and records the meals it empties so the coverage rule does not reject a meal the user removed on purpose.
/// </summary>
public static class AgentRemovalPolicy
{
    private static readonly Regex Removal = new(@"^(?:(?:hay|toi muon|minh muon|cho toi) )?(?:xoa|bo|huy)(?! sung\b) (?<target>.+?)(?: (?:di|nhe|nha|giup|giup toi|luon|voi))*$", RegexOptions.CultureInvariant);
    private static readonly Regex RemovalWord = new(@"\b(?:xoa|bo|huy)\b(?! sung\b)", RegexOptions.CultureInvariant);
    private static readonly Regex MealReference = new(@"bua\s+\S+(?:\s+\S+)?\s*,?\s*(?:(?:cua|trong|vao)\s+)?ngay\s+(?:thu\s+)?\d{1,3}|ngay\s+(?:thu\s+)?\d{1,3}\s*,?\s*(?:(?:vao|o)\s+)?bua\s+\S+(?:\s+\S+)?", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> TargetFillers = ["mon", "cac", "o", "cua", "trong", "va", "cai"];
    private static readonly Regex WholeDay = new(@"^(?:(?:cac|tat ca(?: cac)?) mon (?:cua |trong )?)?ngay (?:thu )?(?<day>\d{1,3})$", RegexOptions.CultureInvariant);
    private static readonly Regex MealWithoutDay = new(@"^bua (?<meal>sang|trua|toi|phu|khuya)(?<all> (?:(?:cua |o |trong )?(?:ca chuyen|toan chuyen|toan bo|moi ngay|tat ca(?: cac)? ngay|cac ngay)))?$", RegexOptions.CultureInvariant);

    /// <summary>The source items a whole-message removal names, or null when the model must read the message.</summary>
    public static AgentItem[]? Targets(AgentContext context)
    {
        // STEP-01: Return null early when context.Source is not { } source, source.Items.Length is 0, context.ReplanAfterConfirmation is true or 2 more condition(s).
        if (context.Source is not { } source || source.Items.Length == 0 || context.ReplanAfterConfirmation ||
            context.State.PendingStateChange is not null || context.State.PendingPlanningRequest is not null) return null;
        // STEP-02: Prepare text through Regex.Replace().Trim and match through Removal.Match.
        var text = Regex.Replace(Normalize(context.Request.Content), @"[.!?]+$", "").Trim();
        var match = Removal.Match(text);
        // STEP-03: Return null early when match.Success is false.
        if (!match.Success) return null;
        // STEP-04: Prepare target and items through ItemsNamedBy.
        var target = match.Groups["target"].Value;
        var items = ItemsNamedBy(context, source, target);
        // STEP-05: Return one of two results depending on items has the expected shape and items.Any does not match the filter.
        // Món/quán người dùng đã yêu cầu theo tên là ràng buộc: để model đề xuất đổi và backend hỏi xác nhận như mọi thay đổi ràng buộc khác.
        return items is { Length: > 0 } && !items.Any(item => AgentScopedPlanning.Request(context.State, item.DayNumber, item.MealSlot) is { } request &&
            (request.DishName ?? request.RestaurantName) is not null) ? items : null;
    }

    /// <summary>Asks which days are meant by "bỏ bữa sáng" over a plan of several days, which the model used to read as replacing one dish.</summary>
    public static string? Question(AgentContext context)
    {
        // STEP-01: Return null early when context.Source is not { } source, source.Items.Length is 0, context.State.PendingStateChange exists or 1 more condition(s).
        if (context.Source is not { } source || source.Items.Length == 0 || context.State.PendingStateChange is not null || context.State.PendingPlanningRequest is not null) return null;
        // STEP-02: Prepare match through Removal.Match.
        var match = Removal.Match(Regex.Replace(Normalize(context.Request.Content), @"[.!?]+$", "").Trim());
        // STEP-03: Return null early when match.Success is false, MealWithoutDay.Match() is not { Success: true } meal or meal.Groups["all"].Success is true.
        if (!match.Success || MealWithoutDay.Match(match.Groups["target"].Value) is not { Success: true } meal || meal.Groups["all"].Success) return null;
        // STEP-04: Prepare slot through Meal.
        var slot = Meal(meal.Groups["meal"].Value);
        // STEP-05: Return one of two results depending on source.Items.Any matches the filter.
        return source.Items.Any(item => item.MealSlot == slot)
            ? $"Bạn muốn bỏ {AgentReplyBuilder.Meal(slot)} của tất cả các ngày hay ngày cụ thể nào?" : null;
    }

    /// <summary>True when the message asks to remove something, so a model patch that empties a meal is honoured.</summary>
    public static bool StatesRemoval(string content) => RemovalWord.IsMatch(Normalize(content));

    /// <summary>Adds the meals the remaining items no longer cover to the skipped meals, dropping meals that have dishes again.</summary>
    public static AgentState WithSkippedMeals(AgentState state, AgentSnapshot source, IReadOnlyCollection<AgentItem> remaining)
    {
        var emptied = source.Items.Select(item => new AgentScope(item.DayNumber, item.MealSlot))
            .Where(slot => !remaining.Any(item => item.DayNumber == slot.DayNumber && item.MealSlot == slot.MealSlot));
        return state with { SkippedMeals = Uncovered((state.SkippedMeals ?? []).Concat(emptied).Distinct(), remaining) };
    }

    /// <summary>The skipped meals that still have no dish; null when none remain, so state stays free of empty backend fields.</summary>
    public static AgentScope[]? Uncovered(IEnumerable<AgentScope> meals, IReadOnlyCollection<AgentItem> items)
    {
        var empty = meals.Where(slot => !items.Any(item => item.DayNumber == slot.DayNumber && item.MealSlot == slot.MealSlot)).ToArray();
        return empty.Length == 0 ? null : empty;
    }

    private static AgentItem[]? ItemsNamedBy(AgentContext context, AgentSnapshot source, string target)
    {
        // STEP-01: Return one of two results depending on target.Split().All matches the filter early when context.Request.SelectedItemKey exists.
        if (context.Request.SelectedItemKey is { } key)
            return target.Split(' ').All(word => word is "mon" or "nay" or "cai" or "do" or "ay") ? source.Items.Where(item => item.ItemKey == key).ToArray() : null;
        // STEP-02: Prepare name through Regex.Replace and named through source.Items.Where(filtered).ToArray.
        var name = Regex.Replace(target, @"^mon ", "");
        var named = source.Items.Where(item => item.DishName is not null && Normalize(item.DishName) == name).ToArray();
        // STEP-03: Return named early when named.Length > 0.
        if (named.Length > 0) return named;
        // STEP-04: Return the result of source.Items.Where(filtered).ToArray early when WholeDay.Match() has the expected shape.
        if (WholeDay.Match(target) is { Success: true } day) return source.Items.Where(item => item.DayNumber.ToString(CultureInfo.InvariantCulture) == day.Groups["day"].Value).ToArray();
        // STEP-05: Return the result of source.Items.Where(filtered).ToArray early when MealWithoutDay.Match() has the expected shape and everyDay.Groups["all"].Success is true.
        if (MealWithoutDay.Match(target) is { Success: true } everyDay && everyDay.Groups["all"].Success)
            return source.Items.Where(item => item.MealSlot == Meal(everyDay.Groups["meal"].Value)).ToArray();
        // STEP-06: Prepare remainder through MealReference.Replace.
        // Chỉ nhận khi cả câu chỉ gồm các bữa kèm ngày; còn từ khác ("bữa tối ngày 2 thay bằng phở") thì để model đọc.
        var remainder = MealReference.Replace(target, " ");
        // STEP-07: Return null early when target does not match MealReference or Regex.Split().Where(filtered).All does not match the filter.
        if (!MealReference.IsMatch(target) || !Regex.Split(remainder, @"[\s,]+").Where(word => word.Length > 0).All(TargetFillers.Contains)) return null;
        // STEP-08: Prepare meals from AgentMealScopePolicy.Read.
        var meals = AgentMealScopePolicy.Read(target, context.State).Meals;
        // STEP-09: Return one of two results depending on meals.Length is 0.
        return meals.Length == 0 ? null : source.Items.Where(item => meals.Any(meal => meal.DayNumber == item.DayNumber && meal.MealSlot == item.MealSlot)).ToArray();
    }

    private static string Meal(string word) => word switch { "sang" => "breakfast", "trua" => "lunch", "toi" => "dinner", "phu" => "snack", _ => "late_night" };

    private static string Normalize(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim();
}
