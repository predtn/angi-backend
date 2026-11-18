using ANGI.Application.Common.Models.Discovery;
using ANGI.Domain.Entities;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Discovery;

/// <summary>Verifies MealHours decides whether business hours reach a meal, per weekday, across midnight and without hours.</summary>
public sealed class MealHoursTests
{
    private static RestaurantBusinessHour Hour(short day, int open, int close) =>
        new() { DayOfWeek = day, OpenTime = new(open, 0), CloseTime = new(close, 0) };

    private static RestaurantBusinessHour[] EveryDay(int open, int close) =>
        Enumerable.Range(0, 7).Select(day => Hour((short)day, open, close)).ToArray();

    // TEST-01: Being open for part of the meal is enough; hours that never reach it are not.
    /// <summary>Ensures a morning-only restaurant serves breakfast and lunch's start but not dinner.</summary>
    [Theory]
    [InlineData("breakfast", true)]
    [InlineData("lunch", true)]
    [InlineData("snack", false)]
    [InlineData("dinner", false)]
    [InlineData("late_night", false)]
    public void IsOpen_MorningOnly_MatchesOverlappingMeals(string meal, bool expected)
    {
        MealHours.IsOpen(EveryDay(6, 11), meal, []).Should().Be(expected);
    }

    // TEST-02: A restaurant without business hours and an unknown meal are never left out.
    /// <summary>Ensures missing data does not remove a restaurant.</summary>
    [Fact]
    public void IsOpen_NoHoursOrUnknownMeal_IsOpen()
    {
        MealHours.IsOpen([], "dinner", [1]).Should().BeTrue();
        MealHours.IsOpen(EveryDay(6, 11), null, [1]).Should().BeTrue();
        MealHours.IsOpen(EveryDay(6, 11), "brunch", [1]).Should().BeTrue();
    }

    // TEST-03: A known weekday must be served; with none known, one day of the week is enough.
    /// <summary>Ensures a restaurant closed on Sunday is left out of a Sunday meal only.</summary>
    [Fact]
    public void IsOpen_ClosedOnSunday_DependsOnWeekday()
    {
        var hours = Enumerable.Range(1, 6).Select(day => Hour((short)day, 10, 21)).ToArray();

        MealHours.IsOpen(hours, "lunch", [0]).Should().BeFalse();
        MealHours.IsOpen(hours, "lunch", [1]).Should().BeTrue();
        MealHours.IsOpen(hours, "lunch", [1, 0]).Should().BeFalse();
        MealHours.IsOpen(hours, "lunch", []).Should().BeTrue();
    }

    // TEST-04: Rows and meals that run past midnight are read across the day boundary.
    /// <summary>Ensures a night restaurant serves late night, and its spill-over reaches the next day's start but not breakfast.</summary>
    [Fact]
    public void IsOpen_PastMidnight_ReadsAcrossDays()
    {
        // Open Friday 22:00 to Saturday 03:00 only.
        RestaurantBusinessHour[] friday = [Hour(5, 22, 3)];
        MealHours.IsOpen(friday, "late_night", [5]).Should().BeTrue();
        MealHours.IsOpen(friday, "dinner", [5]).Should().BeFalse();
        MealHours.IsOpen(friday, "breakfast", [6]).Should().BeFalse();
        MealHours.IsOpen(friday, "late_night", [6]).Should().BeFalse();

        // Open Saturday 00:00 to 01:00 reaches Friday's late night, which runs to 02:00.
        RestaurantBusinessHour[] earlySaturday = [Hour(6, 0, 1)];
        MealHours.IsOpen(earlySaturday, "late_night", [5]).Should().BeTrue();

        // Open Thursday 18:00 to Friday 07:00 reaches Friday's breakfast.
        RestaurantBusinessHour[] overnight = [Hour(4, 18, 7)];
        MealHours.IsOpen(overnight, "breakfast", [5]).Should().BeTrue();
        MealHours.IsOpen(overnight, "breakfast", [4]).Should().BeFalse();
    }

    // TEST-05: Synthetic 06:00-23:00 hours of the local catalog leave every meal open.
    /// <summary>Ensures the local seed's hours remove no dish from any meal.</summary>
    [Theory]
    [InlineData("breakfast")]
    [InlineData("lunch")]
    [InlineData("snack")]
    [InlineData("dinner")]
    [InlineData("late_night")]
    public void IsOpen_LocalSeedHours_ServesEveryMeal(string meal)
    {
        MealHours.IsOpen(EveryDay(6, 23), meal, [0, 3, 6]).Should().BeTrue();
    }

    // TEST-05b: A moment falls in one meal, or none between meals; "open now" is the same rule on a one-minute window.
    /// <summary>Ensures 23:05 and 01:30 are late night, 10:15 is between meals, and a 06:00-10:00 restaurant is open at 09:59 but not at 10:00.</summary>
    [Fact]
    public void MealAtAndAt_Moment_FollowMealWindows()
    {
        MealHours.MealAt(new(8, 0)).Should().Be("breakfast");
        MealHours.MealAt(new(14, 0)).Should().Be("snack");
        MealHours.MealAt(new(23, 5)).Should().Be("late_night");
        MealHours.MealAt(new(1, 30)).Should().Be("late_night");
        MealHours.MealAt(new(10, 15)).Should().BeNull();
        MealHours.MealAt(new(4, 0)).Should().BeNull();

        RestaurantBusinessHour[] morning = [Hour(1, 6, 10)];
        morning.Any(MealHours.Overlaps(MealHours.At(new(9, 59)), 1).Compile()).Should().BeTrue();
        morning.Any(MealHours.Overlaps(MealHours.At(new(10, 0)), 1).Compile()).Should().BeFalse();
        // Open Monday 19:00 to Tuesday 03:00: open at 02:30 on Tuesday, not on Monday.
        RestaurantBusinessHour[] night = [Hour(1, 19, 3)];
        night.Any(MealHours.Overlaps(MealHours.At(new(2, 30)), 2).Compile()).Should().BeTrue();
        night.Any(MealHours.Overlaps(MealHours.At(new(2, 30)), 1).Compile()).Should().BeFalse();
        night.Any(MealHours.Overlaps(MealHours.At(new(23, 59)), 1).Compile()).Should().BeTrue();
    }

    // TEST-05c: "Open now" needs the restaurant open this minute and for 30 more, also across midnight.
    /// <summary>Ensures a restaurant closing within 30 minutes or not open yet is left out, and exactly 30 minutes left is enough.</summary>
    [Theory]
    [InlineData(1, 9, 11, 1, 10, 29, true)]
    [InlineData(1, 9, 11, 1, 10, 30, true)]
    [InlineData(1, 9, 11, 1, 10, 31, false)]
    [InlineData(1, 9, 11, 1, 8, 50, false)]
    [InlineData(1, 22, 2, 1, 23, 50, true)]
    [InlineData(1, 22, 2, 2, 1, 30, true)]
    [InlineData(1, 22, 2, 2, 1, 31, false)]
    [InlineData(1, 18, 0, 1, 23, 30, true)]
    [InlineData(1, 18, 0, 1, 23, 31, false)]
    public void StaysOpen_MinutesLeft_DecidesOpenNow(int rowDay, int open, int close, int day, int hour, int minute, bool expected)
    {
        RestaurantBusinessHour[] hours = [Hour((short)rowDay, open, close)];

        MealHours.StaysOpen(new(hour, minute), (short)day)
            .All(check => hours.Any(MealHours.Overlaps(check.Window, check.Day).Compile()))
            .Should().Be(expected);
    }

    // TEST-06: Trip days map to weekdays only once the start date is known.
    /// <summary>Ensures day 1 is the start date's weekday and no date gives no weekday.</summary>
    [Fact]
    public void Weekdays_StartDate_MapsTripDays()
    {
        // 2026-11-16 is a Monday.
        MealHours.Weekdays(new DateOnly(2026, 11, 16), [1, 2, 7]).Should().Equal(0, 1, 2);
        MealHours.Weekdays(null, [1, 2]).Should().BeEmpty();
    }
}
