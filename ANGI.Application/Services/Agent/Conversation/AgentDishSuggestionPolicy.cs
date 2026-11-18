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
    // A list asked for with no number shows 10 dishes; a stated number is followed up to this cap.
    public const int MaxCount = 20;
    // At most this many dishes of one restaurant in one list, so ten ideas are not four dishes of the same shop (Round 3 bug report).
    public const int MaxPerRestaurant = 2;
    private static readonly string[] MainMeals = ["breakfast", "lunch", "dinner"];

    private static readonly HashSet<string> GenericNameWords =
        ["mot", "1", "cai", "mon", "an", "do", "thuc", "quan", "bat", "ki", "ky", "nao", "gi", "ngon", "cung", "duoc", "tuy", "ngau", "nhien"];
    // Model viết câu quanh các ô này, backend điền dữ kiện: tên, quán và giá không thể sai hay bị tráo giữa các món.
    private static readonly Regex Slot = new(@"\{(?<field>name|restaurant|price|address|meals)(?<dish>\d{1,2})\}", RegexOptions.CultureInvariant);
    private static readonly Regex PriceUnit = new(@"(\{price\d{1,2}\})\s*(?:đồng|đ|vnđ|vnd)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>True when a catalog name is only generic words ("món ăn bất kì"), so a lookup could never find it.</summary>
    public static bool IsGenericName(string name)
    {
        var words = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(GenericNameWords.Contains);
    }

    /// <summary>States the chosen dishes with their backend-owned restaurant, price, address and meals, and says they follow the user's taste only when they were ranked by it.</summary>
    public static string Reply(IReadOnlyList<Dish> dishes, bool aroundPin, bool personalized = false, bool hasPlan = false, string? area = null, string? areaNotFound = null, bool mealAsked = false,
        Func<Dish, double>? distanceKm = null)
    {
        string AddressNote(Dish dish) => Address(dish, area, distanceKm) is { } address ? $" Địa chỉ: {address}." : "";
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
    /// <param name="distanceKm">Around a place the user named: each dish's straight-line distance from it, added to the address so the user sees how near it is.</param>
    public static AgentDishSuggestion Suggestion(IReadOnlyList<Dish> dishes, bool aroundPin, bool personalized, bool hasPlan = false, string? area = null, string? areaNotFound = null, bool mealAsked = false,
        Func<Dish, double>? distanceKm = null) => new(
        dishes.Select(dish => new AgentSuggestedDish(AgentReplyBuilder.Safe(dish.Name), AgentReplyBuilder.Safe(dish.Restaurant.Name), dish.Price,
            Address(dish, area, distanceKm), mealAsked ? null : Meals(dish), dish.Id, dish.RestaurantId)).ToArray(),
        personalized, aroundPin, Reply(dishes, aroundPin, personalized, hasPlan, area, areaNotFound, mealAsked, distanceKm), hasPlan, area, areaNotFound);

    /// <summary>
    /// Joins one suggestion per meal into a one-day suggestion whose reply lists the dishes under each meal. A meal with no dish says so,
    /// and when no meal has any the first empty suggestion stays, so the model can explain it as usual.
    /// </summary>
    public static AgentDishSuggestion ByMeal(IReadOnlyList<(string Meal, AgentDishSuggestion Found)> meals)
    {
        var first = meals[0].Found;
        var withDishes = meals.Where(meal => meal.Found.Dishes.Length > 0).ToArray();
        if (withDishes.Length == 0) return first;
        var personalized = withDishes.All(meal => meal.Found.Personalized);
        var near = first.Area is not null ? $" quanh {AgentReplyBuilder.Safe(first.Area)}" : first.AroundPin ? " quanh điểm bạn đã chấm trên bản đồ" : "";
        var unknown = first.AreaNotFound is null ? "" : $"Mình chưa định vị được “{AgentReplyBuilder.Safe(first.AreaNotFound)}” nên chưa lọc theo khu đó. ";
        var start = unknown + (personalized ? "Theo khẩu vị của bạn, mình gợi ý" : "Mình gợi ý") + $" các món cho một ngày{near} nhé:";
        var groups = meals.Select(meal =>
        {
            var label = AgentReplyBuilder.Meal(meal.Meal);
            var lines = meal.Found.Dishes.Select(dish => $"- {dish.Name} ở {dish.Restaurant}, khoảng {AgentReplyBuilder.Money(dish.Price)} đồng." +
                (dish.Address is null ? "" : $" Địa chỉ: {dish.Address}.")).ToArray();
            return $"{char.ToUpperInvariant(label[0])}{label[1..]}:\n{(lines.Length > 0 ? string.Join('\n', lines) : "- Chưa có món phù hợp.")}";
        });
        var next = first.HasPlan ? "Muốn cho món nào vào lịch thì nhắn mình ngày và bữa nhé." : "Muốn đổi món hoặc lập lịch từ các món này cứ nhắn mình nhé.";
        return first with
        {
            Dishes = [.. meals.SelectMany(meal => meal.Found.Dishes)], Personalized = personalized,
            FixedReply = $"{start}\n\n{string.Join("\n\n", groups)}\n\n{next}", PriceBelow = null, PriceAbove = null, ByMeal = true
        };
    }

    /// <summary>The restaurant's address, with "cách {area} khoảng N km" when the search stayed around a named place.</summary>
    private static string? Address(Dish dish, string? area, Func<Dish, double>? distanceKm)
    {
        var address = string.IsNullOrWhiteSpace(dish.Restaurant.AddressLine) ? null : AgentReplyBuilder.Safe(dish.Restaurant.AddressLine);
        if (distanceKm is null || area is null) return address;
        var near = $"cách {AgentReplyBuilder.Safe(area)} khoảng {Math.Round(distanceKm(dish), 1).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"))} km";
        return address is null ? near : $"{address}, {near}";
    }

    /// <summary>Điền các ô của model bằng dữ kiện món; ô trỏ tới món không có thì để nguyên.</summary>
    public static string FillSlots(string? reply, AgentDishSuggestion suggestion) => Slot.Replace(WithAddresses(Template(reply), suggestion), slot =>
    {
        var index = int.Parse(slot.Groups["dish"].Value) - 1;
        if (index < 0 || index >= suggestion.Dishes.Length) return slot.Value;
        var dish = suggestion.Dishes[index];
        return slot.Groups["field"].Value switch
        {
            "name" => dish.Name, "restaurant" => dish.Restaurant, "price" => $"{AgentReplyBuilder.Money(dish.Price)} đồng",
            "meals" => dish.Meals ?? slot.Value, _ => dish.Address ?? slot.Value
        };
    });

    /// <summary>
    /// The prompt promises that a dish's address is added when the model leaves out {addressN}, because the user must know where to go
    /// (Round 3, AG-62 showed a suggestion with no address). It goes right after that dish's restaurant slot.
    /// </summary>
    private static string WithAddresses(string template, AgentDishSuggestion suggestion)
    {
        for (var index = 0; index < suggestion.Dishes.Length; index++)
        {
            var number = index + 1;
            if (suggestion.Dishes[index].Address is null || template.Contains($"{{address{number}}}", StringComparison.Ordinal)) continue;
            var restaurant = $"{{restaurant{number}}}";
            var at = template.IndexOf(restaurant, StringComparison.Ordinal);
            if (at >= 0) template = template.Insert(at + restaurant.Length, $" ({{address{number}}})");
        }
        return template;
    }

    // Backend điền giá kèm đơn vị, nên bỏ đơn vị model viết thêm để không thành "45.000 đồng đồng".
    private static string Template(string? reply) => PriceUnit.Replace((reply ?? "").Trim(), "$1");

    private static string Named(Dish dish, bool mealAsked) =>
        AgentReplyBuilder.Safe(dish.Name) + (!mealAsked && Meals(dish) is { } meals ? $" (hợp {meals})" : "");


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

    private static string Normalize(string text)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        return Regex.Replace(Regex.Replace(plain, @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
    }
}
