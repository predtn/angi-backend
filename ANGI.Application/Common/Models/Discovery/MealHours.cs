using System.Collections.Concurrent;
using System.Linq.Expressions;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Models.Discovery;

/// <summary>
/// The hours each meal is eaten, and whether a restaurant's business hours reach into them. One expression decides it: the
/// repository sends it to SQL to filter candidates, the Agent validator compiles it to check a plan, so the two never disagree.
/// </summary>
public static class MealHours
{
    /// <summary>The hours of one meal; an end at or before the start runs past midnight.</summary>
    public sealed record Window(TimeOnly Start, TimeOnly End)
    {
        public bool CrossesMidnight => End <= Start;
    }

    // User decision 2026-11-16. A restaurant only has to be open for part of the meal, not all of it.
    private static readonly IReadOnlyDictionary<string, Window> Windows = new Dictionary<string, Window>(StringComparer.Ordinal)
    {
        ["breakfast"] = new(new(6, 0), new(10, 0)),
        ["lunch"] = new(new(10, 30), new(14, 0)),
        ["snack"] = new(new(14, 0), new(17, 0)),
        ["dinner"] = new(new(17, 0), new(21, 0)),
        ["late_night"] = new(new(21, 0), new(2, 0))
    };

    private static readonly ConcurrentDictionary<(string Meal, short? Day), Func<RestaurantBusinessHour, bool>> Compiled = new();

    /// <summary>The hours of a meal code, or null for a code that names no meal.</summary>
    public static Window? For(string? meal) => meal is not null && Windows.TryGetValue(meal, out var window) ? window : null;

    /// <summary>A one-minute window at a moment, so the same rule answers "open right now".</summary>
    public static Window At(TimeOnly time) => new(time, time.AddMinutes(1));

    /// <summary>How long a restaurant must stay open for a "right now" suggestion, so nobody is sent to one about to close (user decision 2026-11-18).</summary>
    public const int MinOpenMinutes = 30;

    /// <summary>
    /// The two moments a restaurant must be open at to count as open now: this minute and the last of the next <see cref="MinOpenMinutes"/>,
    /// which falls on the next weekday when it passes midnight. Hours are stored in whole minutes, so closing exactly then is enough.
    /// </summary>
    public static (Window Window, short? Day)[] StaysOpen(TimeOnly time, short? day)
    {
        var last = time.AddMinutes(MinOpenMinutes - 1);
        var lastDay = last < time && day is { } today ? (short)((today + 1) % 7) : day;
        return [(At(time), day), (At(last), lastDay)];
    }

    /// <summary>The meal whose hours hold the moment, or null between meals (10:00-10:30 and 02:00-06:00).</summary>
    public static string? MealAt(TimeOnly time) => Windows.FirstOrDefault(pair => pair.Value.CrossesMidnight
        ? time >= pair.Value.Start || time < pair.Value.End
        : time >= pair.Value.Start && time < pair.Value.End).Key;

    /// <summary>The weekdays (0 = Sunday, as business hours store them) of the given trip days; empty while the start date is unknown.</summary>
    public static short[] Weekdays(DateOnly? startDate, IEnumerable<short> dayNumbers) => startDate is { } start
        ? dayNumbers.Select(day => (short)start.AddDays(day - 1).DayOfWeek).Distinct().Order().ToArray()
        : [];

    /// <summary>
    /// Whether a business-hour row reaches into the meal on the given weekday, or on any weekday when day is null.
    /// A row whose close is at or before its open runs past midnight, so it also covers the start of the next day.
    /// </summary>
    public static Expression<Func<RestaurantBusinessHour, bool>> Overlaps(Window window, short? day)
    {
        var start = window.Start;
        var end = window.End;
        var anyDay = day is null;
        var same = day ?? 0;
        var previous = (short)((same + 6) % 7);
        var next = (short)((same + 1) % 7);
        if (!window.CrossesMidnight)
            return hour =>
                (anyDay || hour.DayOfWeek == same) && hour.OpenTime < end && (hour.CloseTime <= hour.OpenTime || hour.CloseTime > start) ||
                (anyDay || hour.DayOfWeek == previous) && hour.CloseTime <= hour.OpenTime && hour.CloseTime > start;
        // A meal past midnight: the same day's row reaches its start, or the next day's row opens before it ends.
        return hour =>
            (anyDay || hour.DayOfWeek == same) && (hour.CloseTime <= hour.OpenTime || hour.CloseTime > start) ||
            (anyDay || hour.DayOfWeek == previous) && hour.CloseTime <= hour.OpenTime && hour.CloseTime > start ||
            (anyDay || hour.DayOfWeek == next) && hour.OpenTime < end;
    }

    /// <summary>
    /// Whether a restaurant serves the meal on every given weekday, or on at least one weekday when none is given.
    /// A restaurant without business hours is never left out, and neither is an unknown meal.
    /// </summary>
    public static bool IsOpen(IReadOnlyCollection<RestaurantBusinessHour> hours, string? meal, IReadOnlyCollection<short> weekdays)
    {
        if (hours.Count == 0 || meal is null || For(meal) is not { } window) return true;
        IEnumerable<short?> days = weekdays.Count > 0 ? weekdays.Select(day => (short?)day) : [null];
        return days.All(day => hours.Any(Compiled.GetOrAdd((meal, day), key => Overlaps(window, key.Day).Compile())));
    }
}
