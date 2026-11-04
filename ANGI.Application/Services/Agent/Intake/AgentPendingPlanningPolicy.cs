using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent.Intake;

/// <summary>Retains a request without applying it while taste is unresolved. Only explicit numeric trip facts are extracted.</summary>
public static class AgentPendingPlanningPolicy
{
    private const string Money = @"(?<amount>\d+(?:[.,]\d+)*)(?:\s*(?<unit>trieu|tr|nghin|ngan|k))?\s*(?:dong|vnd)?";
    // Số tiền bắt buộc có đơn vị, dùng cho câu trả lời không kèm chữ "ngân sách" để "4 ngày" không bị đọc thành tiền.
    private const string MoneyWithUnit = @"(?<amount>\d+(?:[.,]\d+)*)\s*(?:(?<unit>trieu|tr|nghin|ngan|k)\s*(?:dong|vnd)?|dong|vnd)";
    // "ngay" right after the amount ("500000 dong ngay", "500k ngay") is also per day, but not "ngay dau/cuoi/thu/2"
    // (a single day of the trip); "500k 4 ngay" stays a total because only 1/mot may sit between amount and "ngay".
    private const string Daily = @"(?<daily>\s*(?:/\s*|(?:cho\s+)?(?:moi|1|mot)\s+)?ngay\b(?!\s+(?:dau|cuoi|thu|\d)))?";
    private const string MentionedDays = @"(?<!(?:\d|k|trieu|tr|nghin|ngan|dong|vnd)\s*)(?<!/\s*)\b\d{1,2}\s+ngay\b(?!\s+(?:dau|cuoi))";
    private const string Unrestricted = @"\b(?:khong gioi han|thoai mai|tuy y)\s+(?:ve\s+)?ngan sach\b|\bngan sach\s*(?:la\s*)?(?:khong gioi han|thoai mai|tuy y)\b";

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
        var budget = Regex.Matches(text, @"\bngan sach\s*(?:(?:la|khoang|tam|toi da)\s*|:\s*)?" + Money + Daily, RegexOptions.CultureInvariant)
            .Cast<Match>().LastOrDefault(match => !Negated(text, match.Index));
        // Trả lời câu hỏi ngân sách bằng một mệnh đề chỉ có số tiền ("500k 1 ngày", "khoảng 2 triệu").
        // "món dưới 50k" không khớp vì mệnh đề không bắt đầu bằng số tiền, nên trần giá món không thành ngân sách chuyến.
        budget ??= Regex.Matches(text, @"(?:^|[,;.!?\n])\s*(?:(?:khoang|tam|toi da|la)\s+)?" + MoneyWithUnit + Daily +
            @"\s*(?:nhe|nha|thoi)?\s*(?=[,;.!?\n]|$)", RegexOptions.CultureInvariant)
            .Cast<Match>().LastOrDefault(match => !Negated(text, match.Index));
        if (budget is not null && ParseMoney(budget, out var amount))
        {
            daily = budget.Groups["daily"].Success ? amount : null;
            total = budget.Groups["daily"].Success ? null : amount;
        }
        return new(messages, days, total, daily);
    }

    /// <summary>
    /// Reads a reply to a trip question: the wording is known to be about the trip, so "3 ngày 1 triệu" and "Hà Nội 3 ngày" count,
    /// which <see cref="Capture"/> leaves out because it also runs on ordinary edit messages.
    /// </summary>
    public static AgentPendingPlanningRequest CaptureAnswer(AgentPendingPlanningRequest? previous, string content, bool budgetAsked)
    {
        var request = Capture(previous, content);
        var text = Normalize(content).Trim();
        var dailyFirst = Regex.Match(text, @"\b(?:moi|1|mot)\s+ngay\s*(?:(?:khoang|tam|toi da|la)\s+)?" + MoneyWithUnit, RegexOptions.CultureInvariant);
        // "1 ngày 200k" là ngân sách theo ngày, không phải chuyến đi một ngày như Capture đọc.
        if (dailyFirst.Success) request = request with { NumDays = previous?.NumDays };
        if (request.NumDays == previous?.NumDays)
        {
            var days = Regex.Matches(text, MentionedDays + @"|\b(?:[2-9]|[12]\d|30)\s+ngay\b(?!\s+(?:dau|cuoi))", RegexOptions.CultureInvariant).Cast<Match>()
                .LastOrDefault(match => !Negated(text, match.Index) && !(dailyFirst.Success && match.Index == dailyFirst.Index));
            if (days is not null && short.TryParse(new string(days.Value.Where(char.IsDigit).ToArray()), out var parsed)) request = request with { NumDays = parsed };
        }
        if (request.TotalBudget != previous?.TotalBudget || request.DailyBudget != previous?.DailyBudget) return request;
        if (dailyFirst.Success && ParseMoney(dailyFirst, out var perDay)) return request with { DailyBudget = perDay, TotalBudget = null };
        // Số tiền nằm trong câu ("3 ngày 1 triệu"); "dưới 50k mỗi món" là trần giá món nên không tính.
        var budget = Regex.Matches(text, MoneyWithUnit + Daily + @"(?!\s*(?:/\s*)?(?:(?:moi|1|mot)\s+)?(?:mon|bua)\b)", RegexOptions.CultureInvariant).Cast<Match>()
            .LastOrDefault(match => !Negated(text, match.Index) && !Regex.IsMatch(text[..match.Index], @"\b(?:mon|bua)\b", RegexOptions.CultureInvariant));
        if (budget is not null && ParseMoney(budget, out var amount))
            return budget.Groups["daily"].Success ? request with { DailyBudget = amount, TotalBudget = null } : request with { TotalBudget = amount, DailyBudget = null };
        // Trả lời đúng câu hỏi ngân sách bằng một con số trần ("1500000").
        if (budgetAsked && Regex.IsMatch(text, @"^(?:\d{4,}|\d{1,3}(?:[.,]\d{3})+)$", RegexOptions.CultureInvariant) &&
            decimal.TryParse(text.Replace(".", "").Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var plain) && plain <= AgentStateValidator.MaxMoney)
            return request with { TotalBudget = plain, DailyBudget = null };
        return request;
    }

    /// <summary>True when the reply gives no budget limit on purpose; state policy still asks for its separate confirmation.</summary>
    public static bool StatesUnrestricted(string content)
    {
        var text = Normalize(content).Trim();
        return Regex.IsMatch(text, Unrestricted, RegexOptions.CultureInvariant) ||
            Regex.IsMatch(text, @"(?:^|[,;.!?\n])\s*(?:khong gioi han|thoai mai|tuy y|bao nhieu cung duoc|sao cung duoc|xa lang|tha ga|vo tu|khong lo)(?:\s+(?:nhe|nha|di|luon))?\s*(?=[,;.!?\n]|$)", RegexOptions.CultureInvariant);
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

    /// <summary>
    /// Duration and budget stated in the current message override the model's reading of them, which once turned "500k 1 ngày"
    /// into a 1,500 đồng trip budget. Constraint confirmation still applies to the resulting state.
    /// </summary>
    public static AgentState? ApplyStated(AgentState? proposed, string content)
    {
        if (proposed is null) return null;
        var stated = Capture(null, content);
        return stated.NumDays.HasValue || stated.TotalBudget.HasValue || stated.DailyBudget.HasValue ? Apply(proposed, stated) : proposed;
    }

    /// <summary>Asks the duration/budget still unknown after the retained request; province is left to the planning turn.</summary>
    public static string? MissingTripDetails(AgentState state, AgentPendingPlanningRequest request)
    {
        var known = Apply(state, request);
        var texts = request.Messages.Select(Normalize).ToArray();
        // Bản sao chỉ để quyết định có hỏi không; state thật không đổi. Capture chỉ ghi số ngày ở mẫu câu chặt vì nó ghi đè state,
        // còn ở đây chỉ cần biết người dùng đã nói "4 ngày" ("Hà Nội 4 ngày") để không hỏi lại; "500k 1 ngày" là ngân sách theo ngày.
        if (!known.NumDays.HasValue && texts.Any(text => Regex.IsMatch(text, MentionedDays, RegexOptions.CultureInvariant)))
            known = known with { NumDays = 1 };
        // Ngân sách theo ngày khi chưa biết số ngày đã đủ; "ngân sách không giới hạn" vẫn cần xác nhận riêng theo chính sách ràng buộc.
        if (request.DailyBudget.HasValue || texts.Any(text => Regex.IsMatch(text, Unrestricted, RegexOptions.CultureInvariant)))
            known = known with { BudgetUnrestricted = true };
        return AgentReplyBuilder.TripDetailsQuestion(known, askProvince: false);
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
