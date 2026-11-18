using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent.Catalog;

/// <summary>
/// Finds dish names that share words with a name the catalog does not hold ("cà phê sữa đá" when ANGI calls it "Cà Phê Nâu Đá").
/// It only reads words and orders the list: the model reads the names and picks the ones that mean the same dish (user decision 2026-11-16).
/// </summary>
public static class AgentNearDishes
{
    /// <summary>How many names one search returns at most.</summary>
    public const int MaxNames = 25;

    /// <summary>
    /// Scores each dish: 2 per wanted word in its name, 1 per wanted word only in its description or tags; a dish needs 2 points.
    /// Words are compared with their accents when the query has accents, so "cà" never matches "cá"; an unaccented query is compared without accents.
    /// Branches of one dish become one name with its lowest price and number of restaurants. Among equal scores, a name that starts with the query's
    /// first word comes first, because a Vietnamese dish name starts with its kind ("trà tắc" lists "Trà Quất Mật Ong" before "Bánh Crepe Trà Xanh").
    /// </summary>
    public static AgentNearDish[] Find(string query, IEnumerable<AgentDishText> dishes, int limit = MaxNames)
    {
        var fold = Lower(query) == Fold(query);
        var wanted = Words(query, fold);
        if (wanted.Count == 0) return [];
        var head = FirstWord(query, fold);
        return dishes
            .Select(dish =>
            {
                var name = Words(dish.Name, fold);
                var text = Words(string.Join(' ', dish.Tags.Prepend(dish.Description ?? "")), fold);
                var score = wanted.Sum(word => name.Contains(word) ? 2 : text.Contains(word) ? 1 : 0);
                return (Dish: dish, Score: score);
            })
            .Where(x => x.Score >= 2)
            .GroupBy(x => Lower(x.Dish.Name.Trim()))
            .Select(group =>
            {
                var best = group.MaxBy(x => x.Score);
                return new AgentNearDish(best.Dish.Name.Trim(), Short(best.Dish.Description), group.Min(x => x.Dish.Price),
                    group.Select(x => x.Dish.RestaurantId).Distinct().Count(), best.Score);
            })
            .OrderByDescending(x => x.Score).ThenByDescending(x => FirstWord(x.Name, fold) == head).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    /// <summary>Whether a catalog name is one of the names the model chose, ignoring case, spacing and Unicode form.</summary>
    public static bool SameName(string a, string b) =>
        string.Join(' ', Words(a, false)) == string.Join(' ', Words(b, false));

    private static HashSet<string> Words(string text, bool fold) =>
        Regex.Split(fold ? Fold(text) : Lower(text), @"[^\p{L}\p{N}]+").Where(word => word.Length > 0).ToHashSet(StringComparer.Ordinal);

    private static string? FirstWord(string text, bool fold) =>
        Regex.Split(fold ? Fold(text) : Lower(text), @"[^\p{L}\p{N}]+").FirstOrDefault(word => word.Length > 0);

    private static string Lower(string text) => text.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private static string Fold(string text) => new(Lower(text).Replace('đ', 'd').Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

    private static string? Short(string? description) => description is { Length: > 60 } ? description[..60] : description;
}
