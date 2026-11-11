using System.Globalization;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Business-hour rules shared by OWN-01 (VALIDATION_FAILED) and OWN-04 (INVALID_BUSINESS_HOURS).</summary>
internal static class BusinessHourRules
{
    private const int MinutesPerDay = 24 * 60;
    private const int MinutesPerWeek = 7 * MinutesPerDay;

    /// <summary>Parses an exact 24-hour HH:mm value.</summary>
    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>
    /// Whether two intervals share any minute on the circular seven-day schedule. closeTime &lt; openTime runs past
    /// midnight, so Monday 22:00-02:00 overlaps Tuesday 01:00-05:00. Intervals with an unparsable time are ignored;
    /// the field validator reports them.
    /// </summary>
    public static bool HasOverlap(IEnumerable<BusinessHourDto>? hours)
    {
        if (hours is null)
        {
            return false;
        }

        var intervals = new List<(int Start, int End)>();
        foreach (var hour in hours)
        {
            if (hour is null || !TryParseTime(hour.OpenTime, out var openTime) || !TryParseTime(hour.CloseTime, out var closeTime))
            {
                continue;
            }

            var start = (hour.DayOfWeek * MinutesPerDay) + (openTime.Hour * 60) + openTime.Minute;
            var end = (hour.DayOfWeek * MinutesPerDay) + (closeTime.Hour * 60) + closeTime.Minute;
            if (end <= start)
            {
                end += MinutesPerDay;
            }

            intervals.Add((start, end));
        }

        for (var firstIndex = 0; firstIndex < intervals.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < intervals.Count; secondIndex++)
            {
                var first = intervals[firstIndex];
                var second = intervals[secondIndex];
                foreach (var weekShift in new[] { -MinutesPerWeek, 0, MinutesPerWeek })
                {
                    if (first.Start < second.End + weekShift && second.Start + weekShift < first.End)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
