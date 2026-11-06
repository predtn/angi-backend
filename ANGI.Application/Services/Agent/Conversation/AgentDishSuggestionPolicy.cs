using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Services.Agent.Conversation;

/// <summary>Holds the facts of a dish idea, which needs no trip: the model words the reply around slots the backend fills, and a failed check shows the fixed one.</summary>
public static class AgentDishSuggestionPolicy
{
    public const string NothingFound = "Mình chưa tìm thấy món nào phù hợp trên ANGI. Bạn thử bỏ bớt điều kiện hoặc chọn nơi khác nhé.";
    public const int MaxCount = 5;
    private static readonly string[] MainMeals = ["breakfast", "lunch", "dinner"];

    private static readonly HashSet<string> GenericNameWords =
        ["mot", "1", "cai", "mon", "an", "do", "thuc", "quan", "bat", "ki", "ky", "nao", "gi", "ngon", "cung", "duoc", "tuy", "ngau", "nhien"];
    // Đọc câu của model, không đọc câu người dùng: chưa xếp theo khẩu vị thì không được nói là theo khẩu vị, trừ khi đang nói rõ là chưa.
    private static readonly Regex TasteClaim = new(@"\b(?:theo|dua (?:theo|tren)|hop(?: voi)?|phu hop(?: voi)?|dung)\s+(?:gu|khau vi|so thich)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Denied = new(@"\b(?:chua|khong)\b", RegexOptions.CultureInvariant);
    // Model viết câu quanh các ô này, backend điền dữ kiện: tên, quán và giá không thể sai hay bị tráo giữa các món.
    private static readonly Regex Slot = new(@"\{(?<field>name|restaurant|price|address|meals)(?<dish>\d{1,2})\}", RegexOptions.CultureInvariant);
    private static readonly Regex PriceUnit = new(@"(\{price\d{1,2}\})\s*(?:đồng|đ|vnđ|vnd)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] RequiredSlots = ["name", "restaurant", "price"];

    /// <summary>True when a catalog name is only generic words ("món ăn bất kì"), so a lookup could never find it.</summary>
    public static bool IsGenericName(string name)
    {
        var words = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(GenericNameWords.Contains);
    }

    /// <summary>States the chosen dishes with their backend-owned restaurant, price, address and meals, and says they follow the user's taste only when they were ranked by it.</summary>
    public static string Reply(IReadOnlyList<Dish> dishes, bool aroundPin, bool personalized = false, bool hasPlan = false, string? area = null, string? areaNotFound = null, bool mealAsked = false)
    {
        // STEP-01: Return NothingFound early when dishes.Count is 0.
        if (dishes.Count == 0) return NothingFound;
        // STEP-02: Prepare near, start and next.
        var near = area is not null ? $" quanh {AgentReplyBuilder.Safe(area)}" : aroundPin ? " quanh điểm bạn đã chấm trên bản đồ" : "";
        var unknown = areaNotFound is null ? "" : $"Mình chưa định vị được “{AgentReplyBuilder.Safe(areaNotFound)}” nên chưa lọc theo khu đó. ";
        var start = unknown + (personalized ? "Theo khẩu vị của bạn, mình gợi ý" : "Mình gợi ý");
        var next = hasPlan ? "Muốn cho vào lịch thì nhắn mình ngày và bữa nhé." : "Muốn món khác cứ nhắn mình nhé.";
        // STEP-03: When dishes.Count is 1, return the sentence with its address and meals.
        if (dishes.Count == 1)
        {
            var dish = dishes[0];
            return $"{start} {Named(dish, mealAsked)} ở {AgentReplyBuilder.Safe(dish.Restaurant.Name)}{near}, khoảng {AgentReplyBuilder.Money(dish.Price)} đồng.{AddressNote(dish)} {next}";
        }
        // STEP-04: Prepare lines through dishes.Select.
        var lines = dishes.Select(dish => $"- {Named(dish, mealAsked)} ở {AgentReplyBuilder.Safe(dish.Restaurant.Name)}, khoảng {AgentReplyBuilder.Money(dish.Price)} đồng.{AddressNote(dish)}");
        // STEP-05: Return the formatted text.
        return $"{start} vài món{near} nhé:\n{string.Join('\n', lines)}\n{next}";
    }

    /// <summary>Packs the chosen dishes with the facts a reply may state and the fixed reply to fall back on.</summary>
    public static AgentDishSuggestion Suggestion(IReadOnlyList<Dish> dishes, bool aroundPin, bool personalized, bool hasPlan = false, string? area = null, string? areaNotFound = null, bool mealAsked = false) => new(
        dishes.Select(dish => new AgentSuggestedDish(AgentReplyBuilder.Safe(dish.Name), AgentReplyBuilder.Safe(dish.Restaurant.Name), dish.Price,
            string.IsNullOrWhiteSpace(dish.Restaurant.AddressLine) ? null : AgentReplyBuilder.Safe(dish.Restaurant.AddressLine), mealAsked ? null : Meals(dish))).ToArray(),
        personalized, aroundPin, Reply(dishes, aroundPin, personalized, hasPlan, area, areaNotFound, mealAsked), hasPlan, area, areaNotFound);

    /// <summary>Fills the model's slots with the suggestion's facts when the slots fit and the words around them claim nothing false; otherwise the fixed reply.</summary>
    public static string Worded(string? reply, AgentContext context, AgentDishSuggestion suggestion)
    {
        // STEP-01: When WordingIssue() exists, return the fixed reply, led by the model's own words when it wrote no slot and those words are safe.
        // Không có ô nào mà câu vẫn sạch nghĩa là model chỉ viết lời mở đầu: giữ lời đó, dữ kiện đã có trong câu cố định theo sau.
        if (WordingIssue(reply, context, suggestion) is { } issue)
            return issue == "no_slot" && IsSafeOpening(Template(reply), context, suggestion) ? $"{Template(reply)} {suggestion.FixedReply}" : suggestion.FixedReply;
        // STEP-02: Return the template with each slot filled, after adding the facts the model left out.
        return Slot.Replace(Completed(Template(reply), suggestion.Dishes), slot =>
        {
            var dish = suggestion.Dishes[int.Parse(slot.Groups["dish"].Value) - 1];
            return slot.Groups["field"].Value switch { "name" => dish.Name, "restaurant" => dish.Restaurant, "price" => $"{AgentReplyBuilder.Money(dish.Price)} đồng", "meals" => dish.Meals!, _ => dish.Address! };
        });
    }

    /// <summary>Why the model's sentence cannot be shown, as a short code for the log; null when it can.</summary>
    public static string? WordingIssue(string? reply, AgentContext context, AgentDishSuggestion suggestion)
    {
        // STEP-01: Prepare template through Template.
        var template = Template(reply);
        // STEP-02: Return the code when the slots do not fit, the words around them fail their check or they claim a taste ranking that did not happen.
        return SlotsIssue(Slot.Matches(template), suggestion.Dishes) ?? AgentReplyGuard.TemplateIssue(Slot.Replace(template, " ").Trim(), context) ??
            (!suggestion.Personalized && ClaimsTaste(template) ? "taste_claim" : null);
    }

    private static bool IsSafeOpening(string text, AgentContext context, AgentDishSuggestion suggestion) =>
        AgentReplyGuard.TemplateIssue(text, context) is null && (suggestion.Personalized || !ClaimsTaste(text));

    // Backend điền giá kèm đơn vị, nên bỏ đơn vị model viết thêm để không thành "45.000 đồng đồng".
    private static string Template(string? reply) => PriceUnit.Replace((reply ?? "").Trim(), "$1");

    /// <summary>
    /// Adds where the restaurant is when the model left it out. A reply without an address once left a user asking the same thing three turns running.
    /// Meals are the model's choice: forcing "(hợp bữa sáng)" into every reply read as a form, even late at night.
    /// </summary>
    private static string Completed(string template, IReadOnlyList<AgentSuggestedDish> dishes)
    {
        // STEP-01: For each dish, place its missing address after the restaurant.
        foreach (var (dish, number) in dishes.Select((dish, index) => (dish, index + 1)))
            if (dish.Address is not null && !template.Contains($"{{address{number}}}", StringComparison.Ordinal))
                template = template.Replace($"{{restaurant{number}}}", $"{{restaurant{number}}} ({{address{number}}})", StringComparison.Ordinal);
        // STEP-02: Return template.
        return template;
    }

    /// <summary>What is wrong with the slots: none at all, one pointing at no fact, one dish's slots split by another's, or a dish missing its name, restaurant or price; null when they fit.</summary>
    private static string? SlotsIssue(MatchCollection slots, IReadOnlyList<AgentSuggestedDish> dishes)
    {
        // STEP-01: Prepare used through slots.Select(filtered).ToArray, then return "no_slot" early when a dish exists and no slot does.
        var used = slots.Select(slot => (Field: slot.Groups["field"].Value, Dish: int.Parse(slot.Groups["dish"].Value))).ToArray();
        if (used.Length == 0 && dishes.Count > 0) return "no_slot";
        // STEP-02: Return "unknown_slot" early when a slot names no dish, or an address or meals its dish does not have.
        if (used.Any(slot => slot.Dish < 1 || slot.Dish > dishes.Count || slot.Field == "address" && dishes[slot.Dish - 1].Address is null || slot.Field == "meals" && dishes[slot.Dish - 1].Meals is null)) return "unknown_slot";
        // STEP-03: Return "split_slot" early when one dish's slots are split by another's.
        if (used.Where((slot, index) => index == 0 || slot.Dish != used[index - 1].Dish).Count() != used.Select(slot => slot.Dish).Distinct().Count()) return "split_slot";
        // STEP-04: Return "missing_slot" when a dish lacks its name, restaurant or price.
        return Enumerable.Range(1, dishes.Count).All(dish => RequiredSlots.All(field => used.Contains((field, dish)))) ? null : "missing_slot";
    }

    private static string Named(Dish dish, bool mealAsked) =>
        AgentReplyBuilder.Safe(dish.Name) + (!mealAsked && Meals(dish) is { } meals ? $" (hợp {meals})" : "");

    private static string AddressNote(Dish dish) =>
        string.IsNullOrWhiteSpace(dish.Restaurant.AddressLine) ? "" : $" Địa chỉ: {AgentReplyBuilder.Safe(dish.Restaurant.AddressLine)}.";

    /// <summary>The meals a dish is tagged for, in words; null when it has no such tag or fits every main meal, so there is nothing to warn about.</summary>
    private static string? Meals(Dish dish)
    {
        // STEP-01: Prepare tagged from the dish's meal-time tags.
        var tagged = dish.DishTags.Where(tag => tag.Tag?.TagType == TagType.MealTime).Select(tag => tag.Tag.Code).ToArray();
        // STEP-02: Return null early when nothing limits the dish to some meals.
        if (tagged.Length == 0 || MainMeals.All(tagged.Contains)) return null;
        // STEP-03: Return the labels in meal order.
        return string.Join(", ", UseCases.Validators.Agent.AgentStateValidator.Meals.Where(tagged.Contains).Select(AgentReplyBuilder.Meal));
    }

    private static bool ClaimsTaste(string reply) =>
        Regex.Split(AgentReplyGuard.Plain(reply), @"[.!?\n]").Any(sentence => TasteClaim.IsMatch(sentence) && !Denied.IsMatch(sentence));

    private static string Normalize(string text)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        return Regex.Replace(Regex.Replace(plain, @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
    }
}
