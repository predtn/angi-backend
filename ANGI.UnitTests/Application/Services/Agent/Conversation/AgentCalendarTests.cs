using System.Text.Json;
using ANGI.Application.Services.Agent.Conversation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Checks the calendar the model reads named days from, so it never works out weekdays itself.</summary>
public sealed class AgentCalendarTests
{
    private static (string Date, string Weekday, string Week)[] Days(DateOnly today) => AgentConfirmationPolicy.Calendar(today)
        .Select(day => JsonSerializer.SerializeToElement(day))
        .Select(day => (day.GetProperty("date").GetString()!, day.GetProperty("weekday").GetString()!, day.GetProperty("week").GetString()!)).ToArray();

    // TEST-01: From a Monday the calendar runs to the Sunday of next week, and next Saturday is the one marked "tuần sau".
    /// <summary>Fixes "thứ 7 tuần sau" said on Monday 2026-11-09 being saved as 2026-11-14 (Round 2, AG-48).</summary>
    [Fact]
    public void Calendar_FromMonday_MarksNextWeek()
    {
        // STEP-01: Get days from Days.
        var days = Days(new DateOnly(2026, 11, 9));
        // STEP-02: Assert the first day is today, the range ends on the Sunday of next week and each Saturday has its week.
        days.First().Should().Be(("2026-11-09", "Thứ Hai", "hôm nay"));
        days.Last().Should().Be(("2026-11-22", "Chủ Nhật", "tuần sau"));
        days.Should().Contain(("2026-11-14", "Thứ Bảy", "tuần này")).And.Contain(("2026-11-21", "Thứ Bảy", "tuần sau"));
    }

    // TEST-02: On a Sunday the next day already starts next week.
    /// <summary>Weeks run Monday to Sunday, so a Sunday ends "tuần này".</summary>
    [Fact]
    public void Calendar_FromSunday_NextDayIsNextWeek()
    {
        // STEP-01: Get days from Days.
        var days = Days(new DateOnly(2026, 11, 15));
        // STEP-02: Assert the range is today plus the seven days of next week.
        days.Should().HaveCount(8);
        days[1].Should().Be(("2026-11-16", "Thứ Hai", "tuần sau"));
    }
}
