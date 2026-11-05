using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ANGI.Application.Services.Agent.Proposals;

/// <summary>A start date the user gave for a trip, with the end date when they named a range.</summary>
public sealed record AgentDateAnswer(DateOnly Start, DateOnly? End);

/// <summary>Reads the trip start date from a chat reply ("3/11", "từ 3/11 đến 5/11", "ngày mai"); it never guesses from anything else.</summary>
public static class AgentDatePolicy
{
    private const string Month = @"(?:/|-|\s*thang\s*)(?<{0}>\d{{1,2}})";
    private const string Year = @"(?:(?:/|-|\s*nam\s*)(?<{0}>\d{{4}}|\d{{2}}))?";
    private static readonly string Joiner = @"\s*(?:-|den|toi)\s*";
    private static readonly Regex Iso = new(@"\b(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})\b", RegexOptions.CultureInvariant);
    private static readonly Regex FullRange = new(@"\b(?<d>\d{1,2})" + string.Format(CultureInfo.InvariantCulture, Month, "m") + string.Format(CultureInfo.InvariantCulture, Year, "y") +
        Joiner + @"(?<d2>\d{1,2})" + string.Format(CultureInfo.InvariantCulture, Month, "m2") + string.Format(CultureInfo.InvariantCulture, Year, "y2") + @"\b", RegexOptions.CultureInvariant);
    private static readonly Regex SameMonthRange = new(@"\b(?<d>\d{1,2})" + Joiner + @"(?<d2>\d{1,2})" + string.Format(CultureInfo.InvariantCulture, Month, "m") +
        string.Format(CultureInfo.InvariantCulture, Year, "y") + @"\b", RegexOptions.CultureInvariant);
    private static readonly Regex Single = new(@"\b(?<d>\d{1,2})" + string.Format(CultureInfo.InvariantCulture, Month, "m") + string.Format(CultureInfo.InvariantCulture, Year, "y") + @"\b", RegexOptions.CultureInvariant);
    private const string Lead = @"^(?:(?:di|bat dau|khoi hanh|chon|tu|vao|ngay)\s+)*";
    private static readonly Regex Relative = new(Lead + @"(?<w>hom nay|mai|kia|mot)(?:\s+(?:nhe|nha|di))*$", RegexOptions.CultureInvariant);
    private static readonly Regex Weekday = new(Lead + @"(?:thu\s*(?<w>2|3|4|5|6|7|hai|ba|tu|nam|sau|bay)|(?<w>chu nhat))(?:\s+tuan\s+(?<n>nay|sau|toi))?(?:\s+(?:nhe|nha|di))*$", RegexOptions.CultureInvariant);
    // Một con số trơ trọi ("5", "ngày 5") có thể là ngày trong tháng hoặc ngày thứ 5 của lịch: hỏi lại, không đoán.
    private static readonly Regex Ambiguous = new(Lead + @"\d{1,2}(?:\s+(?:nhe|nha|di))*$", RegexOptions.CultureInvariant);

    /// <summary>Today's calendar date for a Vietnam-based traveler; stored instants remain UTC.</summary>
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);

    /// <summary>Returns the date the message states, or null when it states none or an impossible one such as 31/2.</summary>
    public static AgentDateAnswer? Read(string content, DateOnly today)
    {
        // STEP-01: Prepare text through Normalize.
        var text = Normalize(content);
        // STEP-02: Return one of two results depending on Date() exists early when FullRange.Match() has the expected shape and Date() exists.
        if (FullRange.Match(text) is { Success: true } full && Date(full, "d", "m", "y", today) is { } fullStart)
            return Date(full, "d2", "m2", "y2", fullStart) is { } fullEnd ? new(fullStart, fullEnd) : null;
        // STEP-03: Return one of two results depending on Date() exists early when SameMonthRange.Match() has the expected shape and Date() exists.
        if (SameMonthRange.Match(text) is { Success: true } range && Date(range, "d", "m", "y", today) is { } rangeStart)
            return Date(range, "d2", "m", "y", rangeStart) is { } rangeEnd ? new(rangeStart, rangeEnd) : null;
        // STEP-04: Return one of two results depending on Date() exists early when Iso.Match() has the expected shape.
        if (Iso.Match(text) is { Success: true } iso) return Date(iso, "d", "m", "y", today) is { } isoDate ? new(isoDate, null) : null;
        // STEP-05: Return one of two results depending on Date() exists early when Single.Match() has the expected shape.
        if (Single.Match(text) is { Success: true } single) return Date(single, "d", "m", "y", today) is { } date ? new(date, null) : null;
        // STEP-06: Return the new result early when Relative.Match() has the expected shape.
        if (Relative.Match(text) is { Success: true } relative)
            return new(today.AddDays(relative.Groups["w"].Value switch { "mai" => 1, "kia" or "mot" => 2, _ => 0 }), null);
        // STEP-07: Return one of two results depending on Weekday.Match() has the expected shape.
        return Weekday.Match(text) is { Success: true } weekday ? new(Next(weekday, today), null) : null;
    }

    /// <summary>True when the message tries to name a date but <see cref="Read"/> could not use it, so asking again beats treating it as a new request.</summary>
    public static bool LooksLikeDate(string content)
    {
        var text = Normalize(content);
        return Ambiguous.IsMatch(text) || Single.IsMatch(text) || Iso.IsMatch(text) || SameMonthRange.IsMatch(text) || FullRange.IsMatch(text);
    }

    private static DateOnly? Date(Match match, string day, string month, string year, DateOnly notBefore)
    {
        // STEP-01: Prepare d through int.Parse, m through int.Parse, stated from int.Parse and y.
        var d = int.Parse(match.Groups[day].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups[month].Value, CultureInfo.InvariantCulture);
        var stated = match.Groups[year] is { Success: true } group ? int.Parse(group.Value, CultureInfo.InvariantCulture) : (int?)null;
        var y = stated is { } value ? value < 100 ? 2000 + value : value : notBefore.Year;
        // STEP-02: Return null early when DateOnly.TryParseExact(CultureInfo.InvariantCulture, DateTimeStyles.None, ...) does not hold.
        if (!DateOnly.TryParseExact($"{y:D4}-{m:D2}-{d:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        // STEP-03: Return one of two results depending on stated is missing, date < notBefore and DateOnly.TryParseExact(CultureInfo.InvariantCulture, DateTimeStyles.None, ...) holds.
        // Không nêu năm thì là lần gần nhất sắp tới; năm người dùng nêu thì giữ nguyên để báo "đã qua" thay vì tự đổi.
        return stated is null && date < notBefore && DateOnly.TryParseExact($"{y + 1:D4}-{m:D2}-{d:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var next) ? next : date;
    }

    private static DateOnly Next(Match match, DateOnly today)
    {
        // STEP-01: Prepare wanted.
        var wanted = match.Groups["w"].Value switch
        {
            "2" or "hai" => DayOfWeek.Monday, "3" or "ba" => DayOfWeek.Tuesday, "4" or "tu" => DayOfWeek.Wednesday, "5" or "nam" => DayOfWeek.Thursday,
            "6" or "sau" => DayOfWeek.Friday, "7" or "bay" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday
        };
        // STEP-02: Return the result of monday.AddDays early when match.Groups["n"].Value is "sau" or "toi".
        if (match.Groups["n"].Value is "sau" or "toi")
        {
            // Tuần tính từ thứ Hai, như cách nói "thứ 7 tuần sau".
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) + 7);
            return monday.AddDays(((int)wanted + 6) % 7);
        }
        // STEP-03: Return the result of today.AddDays.
        return today.AddDays(((int)wanted - (int)today.DayOfWeek + 7) % 7);
    }

    private static string Normalize(string text) => Regex.Replace(new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim().TrimEnd('.', '!', '?'), @"\s+", " ");
}
