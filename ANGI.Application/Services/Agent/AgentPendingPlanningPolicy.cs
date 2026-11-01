using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Retains a request without applying it while taste is unresolved. Only explicit numeric trip facts are extracted.</summary>
public static class AgentPendingPlanningPolicy
{
    private const string Money = @"(?<amount>\d+(?:[.,]\d+)*)(?:\s*(?<unit>trieu|tr|nghin|ngan|k))?\s*(?:dong|vnd)?";

    public static AgentPendingPlanningRequest Capture(AgentPendingPlanningRequest? previous, string content)
    {
        var messages = (previous?.Messages ?? []).Append(content.Trim()).ToArray();
        if (messages.Length > 6) messages = [messages[0], .. messages.TakeLast(5)];
        var days = previous?.NumDays;
        var total = previous?.TotalBudget;
        var daily = previous?.DailyBudget;
        var text = Normalize(content);
        var duration = Regex.Matches(text, @"\b(?<days>\d{1,2})\s+ngay\b", RegexOptions.CultureInvariant)
            .Cast<Match>().LastOrDefault(match => TripDuration(text, match));
        if (duration is not null && short.TryParse(duration.Groups["days"].Value, out var parsedDays)) days = parsedDays;
        // "ngay" right after the amount ("500000 dong ngay", "500k ngay") is also per day, but not "ngay dau/cuoi/thu/2"
        // (a single day of the trip); "500k 4 ngay" stays a total because only 1/mot may sit between amount and "ngay".
        var budget = Regex.Matches(text, @"\bngan sach\s*(?:(?:la|khoang|tam|toi da)\s*|:\s*)?" + Money +
            @"(?<daily>\s*(?:/\s*|(?:cho\s+)?(?:moi|1|mot)\s+)?ngay\b(?!\s+(?:dau|cuoi|thu|\d)))?", RegexOptions.CultureInvariant)
            .Cast<Match>().LastOrDefault(match => !Negated(text, match.Index));
        if (budget is not null && ParseMoney(budget, out var amount))
        {
            daily = budget.Groups["daily"].Success ? amount : null;
            total = budget.Groups["daily"].Success ? null : amount;
        }
        return new(messages, days, total, daily);
    }

    /// <summary>Explicit facts awaiting clarification take precedence over a model's copy of the old confirmed state.</summary>
    public static AgentState Apply(AgentState proposed, AgentPendingPlanningRequest request)
    {
        var days = request.NumDays ?? proposed.NumDays;
        var budget = request.TotalBudget ?? (request.DailyBudget.HasValue && days.HasValue
            ? request.DailyBudget.Value * days.Value : proposed.BudgetAmount);
        return proposed with { NumDays = days, BudgetAmount = budget,
            BudgetUnrestricted = request.TotalBudget.HasValue || request.DailyBudget.HasValue ? false : proposed.BudgetUnrestricted };
    }

    private static bool ParseMoney(Match match, out decimal amount)
    {
        var number = match.Groups["amount"].Value;
        var unit = match.Groups["unit"].Value;
        // VND group separators are integral; k/triệu also accept an explicit decimal multiplier.
        var normalized = unit.Length == 0 ? number.Replace(".", "").Replace(",", "") : number.Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)) return false;
        var factor = unit is "trieu" or "tr" ? 1000000m : unit.Length > 0 ? 1000m : 1m;
        if (amount > AgentStateValidator.MaxMoney / factor) return false;
        amount *= factor;
        return decimal.Truncate(amount) == amount;
    }

    private static bool Negated(string text, int index)
    {
        var prefix = text[..index];
        var clause = prefix[(prefix.LastIndexOfAny([',', ';', '.', '!', '?', '\n']) + 1)..];
        return Regex.IsMatch(clause, @"\b(?:khong|dung|chang)\b", RegexOptions.CultureInvariant);
    }

    private static bool TripDuration(string text, Match match)
    {
        if (Negated(text, match.Index) || Regex.IsMatch(text[(match.Index + match.Length)..], @"^\s+(?:dau|cuoi)\b")) return false;
        var prefix = text[..match.Index];
        var clause = prefix[(prefix.LastIndexOfAny([',', ';', '.', '!', '?', '\n']) + 1)..];
        return Regex.IsMatch(clause, @"\b(?:lich|chuyen|hanh trinh|thoi gian)\b", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(clause.Trim(), @"^(?:(?:toi|minh)\s+)?(?:(?:muon|doi|sua|thanh|la|trong)\b\s*)*$", RegexOptions.CultureInvariant);
    }

    private static string Normalize(string content)
    {
        var unquoted = Regex.Replace(content, "\"[^\"]*\"|'[^']*'|“[^”]*”|‘[^’]*’|`[^`]*`", " ");
        return new string(unquoted.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
    }
}
