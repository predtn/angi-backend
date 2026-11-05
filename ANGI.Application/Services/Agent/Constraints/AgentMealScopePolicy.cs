using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent.Constraints;

/// <summary>Reads explicit day/meal wording ("bữa 2 ngày 1", "ngày 1 bữa trưa") so a meal taste is never widened to its whole day.</summary>
public static class AgentMealScopePolicy
{
    private static readonly string[] DefaultMeals = ["breakfast", "lunch", "dinner"];
    private const string Meal = @"bua\s+(?<meal>sang|trua|toi|phu|khuya|(?:thu\s+)?(?:\d{1,2}|nhat|hai|ba|bon|nam)|dau(?:\s+tien)?|cuoi(?:\s+cung)?)\b";
    private const string Day = @"ngay\s+(?:thu\s+)?(?<day>\d{1,3})\b";
    private static readonly Regex MealThenDay = new(@"\b" + Meal + @"\s*,?\s*(?:(?:cua|trong|vao)\s+)?" + Day, RegexOptions.CultureInvariant);
    private static readonly Regex DayThenMeal = new(@"\b" + Day + @"\s*,?\s*(?:(?:vao|o)\s+)?" + Meal, RegexOptions.CultureInvariant);
    private static readonly Regex AnyMeal = new(@"\b" + Meal, RegexOptions.CultureInvariant);
    private static readonly Regex AnyDay = new(@"\b" + Day, RegexOptions.CultureInvariant);

    /// <summary>Meals named together with a day, days named without a meal, and whether a meal number exceeds the meals per day.</summary>
    public static AgentMealReferences Read(string content, AgentState state)
    {
        // STEP-01: Prepare text through Normalize, slots through Slots, meals as List<AgentScope> and covered as List<(int Start, int End)>.
        var text = Normalize(content);
        var slots = Slots(state);
        var meals = new List<AgentScope>();
        var covered = new List<(int Start, int End)>();
        // STEP-02: For each match in MealThenDay.Matches().Concat().OrderBy(filtered), skip this round when covered.Any matches the filter, call covered.Add and 1 more action(s).
        foreach (var match in MealThenDay.Matches(text).Concat(DayThenMeal.Matches(text)).OrderBy(match => match.Index))
        {
            if (covered.Any(span => match.Index < span.End && span.Start < match.Index + match.Length)) continue;
            covered.Add((match.Index, match.Index + match.Length));
            if (Resolve(match.Groups["meal"].Value, slots) is { } meal && short.TryParse(match.Groups["day"].Value, out var day))
                meals.Add(new(day, meal));
        }
        // STEP-03: Prepare days through Select(filtered).Distinct().ToArray and outOfRange through AnyMeal.Matches().Any.
        var days = AnyDay.Matches(text).Where(match => !covered.Any(span => match.Index >= span.Start && match.Index < span.End))
            .Select(match => short.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture)).Distinct().ToArray();
        var outOfRange = AnyMeal.Matches(text).Any(match => Resolve(match.Groups["meal"].Value, slots) is null);
        // STEP-04: Return the new result.
        return new([.. meals.Distinct()], days, outOfRange);
    }

    /// <summary>User wording that may scope a taste this turn: the retained clarification messages, or the current message when it states a taste.</summary>
    public static string[] Evidence(AgentContext context) => context.State.PendingPlanningRequest?.Messages
        ?? (AgentTasteClarificationPolicy.StatesTaste(context.Request.Content) ? [context.Request.Content] : []);

    /// <summary>Turns a whole-day taste the model newly proposed into the meal overrides the user named for that day.</summary>
    public static AgentState? Narrow(AgentState? proposed, AgentState current, IEnumerable<string> messages)
    {
        // STEP-01: Return proposed early when proposed is missing or proposed.TasteOverrides.Length is 0.
        if (proposed is null || proposed.TasteOverrides.Length == 0) return proposed;
        // STEP-02: Prepare references through messages.Select(filtered).ToArray, meals through SelectMany(filtered).Distinct().ToArray and more.
        var references = messages.Select(message => Read(message, proposed)).ToArray();
        var meals = references.SelectMany(x => x.Meals).Distinct().ToArray();
        var days = references.SelectMany(x => x.Days).ToHashSet();
        var overrides = new List<AgentTasteOverride>();
        var changed = false;
        // STEP-03: Go through each item in proposed.TasteOverrides.
        foreach (var item in proposed.TasteOverrides)
        {
            // STEP-04: Prepare named through Where(filtered).Select(filtered).ToArray.
            var named = meals.Where(x => x.DayNumber == item.DayNumber).Select(x => x.MealSlot!).ToArray();
            // STEP-05: Skip this round when item.MealSlot exists, named.Length is 0, days contains item.DayNumber or 2 more condition(s).
            // Chỉ sửa phạm vi model vừa đề xuất ở lượt này. Ngày cũng được nhắc riêng ("cả ngày 1") hoặc bữa đã có khẩu vị riêng thì giữ nguyên.
            if (item.MealSlot is not null || named.Length == 0 || days.Contains(item.DayNumber) || current.TasteOverrides.Any(x => Same(x, item)) ||
                named.Any(meal => proposed.TasteOverrides.Any(x => x.DayNumber == item.DayNumber && x.MealSlot == meal)))
            {
                overrides.Add(item);
                continue;
            }
            // STEP-06: Add the built entries to overrides.
            overrides.AddRange(named.Select(meal => item with { MealSlot = meal }));
            // STEP-07: Set changed.
            changed = true;
        }
        // STEP-08: Return one of two results depending on changed is true.
        return changed ? proposed with { TasteOverrides = [.. overrides] } : proposed;
    }

    /// <summary>Asks which meal is meant when a numbered meal exceeds the meals planned per day, or returns null.</summary>
    public static string? Question(string content, AgentState state)
    {
        // STEP-01: Return null early when Read().OutOfRange is false.
        if (!Read(content, state).OutOfRange) return null;
        // STEP-02: Prepare slots through Slots.
        var slots = Slots(state);
        // STEP-03: Return the formatted text.
        return $"Mỗi ngày trong lịch có {slots.Length} bữa ({string.Join(", ", slots.Select(AgentReplyBuilder.Meal))}). Bạn muốn chọn bữa nào?";
    }

    // "Bữa N" đếm theo các bữa đã khai báo, sắp theo thứ tự sáng → khuya; chưa khai báo thì mặc định sáng, trưa, tối.
    private static string[] Slots(AgentState state) => state.MealSlots.Length > 0
        ? AgentStateValidator.Meals.Where(state.MealSlots.Contains).ToArray() : DefaultMeals;

    private static string? Resolve(string token, string[] slots)
    {
        // STEP-01: Branch on token.
        switch (token)
        {
            case "sang": return "breakfast";
            case "trua": return "lunch";
            case "toi": return "dinner";
            case "phu": return "snack";
            case "khuya": return "late_night";
        }
        // STEP-02: Return slots[0] early when token starts with "dau".
        if (token.StartsWith("dau", StringComparison.Ordinal)) return slots[0];
        // STEP-03: Return slots[^1] early when token starts with "cuoi".
        if (token.StartsWith("cuoi", StringComparison.Ordinal)) return slots[^1];
        // STEP-04: Prepare number from int.TryParse.
        var number = Regex.Replace(token, @"^thu\s+", "", RegexOptions.CultureInvariant) switch
        {
            "nhat" => 1, "hai" => 2, "ba" => 3, "bon" => 4, "nam" => 5,
            var digits => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0
        };
        // STEP-05: Return one of two results depending on number >= 1 and number <= slots.Length.
        return number >= 1 && number <= slots.Length ? slots[number - 1] : null;
    }

    private static bool Same(AgentTasteOverride left, AgentTasteOverride right) =>
        left.DayNumber == right.DayNumber && left.MealSlot == right.MealSlot && left.Targets.SequenceEqual(right.Targets);

    private static string Normalize(string text)
    {
        var unquoted = Regex.Replace(text, "\"[^\"]*\"|'[^']*'|“[^”]*”|‘[^’]*’|`[^`]*`", " ");
        return new string(unquoted.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim();
    }
}

/// <summary>Explicit meal references paired with a day, days named without a meal, and an out-of-range meal number flag.</summary>
public sealed record AgentMealReferences(AgentScope[] Meals, short[] Days, bool OutOfRange);
