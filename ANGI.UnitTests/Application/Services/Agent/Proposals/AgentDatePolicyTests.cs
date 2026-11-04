using FluentAssertions;
using ANGI.Application.Services.Agent.Proposals;

namespace ANGI.UnitTests.Application.Services.Agent.Proposals;

/// <summary>Verifies AgentDatePolicy reads only dates the user really stated, relative to a fixed Tuesday 3 November 2026.</summary>
public sealed class AgentDatePolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    // TEST-01: Single dates are read in every common form; covers slash, dash, words, full year, a two-digit year, ISO, today's date and leading words.
    /// <summary>Lets the user answer the way they would to a person.</summary>
    [Theory]
    [InlineData("3/11", 2026, 11, 3)][InlineData("03/11", 2026, 11, 3)][InlineData("5-11", 2026, 11, 5)][InlineData("5 tháng 11", 2026, 11, 5)]
    [InlineData("ngày 5/11/2026", 2026, 11, 5)][InlineData("5/11/27", 2027, 11, 5)][InlineData("2026-11-05", 2026, 11, 5)][InlineData("đi từ 5/11 nhé", 2026, 11, 5)]
    [InlineData("5 thang 11 nam 2027", 2027, 11, 5)]
    public void Read_SingleDate_ReturnsStart(string content, int year, int month, int day)
    {
        var answer = AgentDatePolicy.Read(content, Today);

        answer!.Start.Should().Be(new DateOnly(year, month, day));
        answer.End.Should().BeNull();
    }

    // TEST-02: Year boundary for a date with no year; covers today, one day before today (next year) and one day after.
    /// <summary>"2/11" said on 3/11 means next year, while a stated year is never moved.</summary>
    [Theory]
    [InlineData("3/11", 2026)][InlineData("2/11", 2027)][InlineData("4/11", 2026)][InlineData("2/11/2026", 2026)]
    public void Read_NoYear_UsesNextOccurrence(string content, int year) =>
        AgentDatePolicy.Read(content, Today)!.Start.Year.Should().Be(year);

    // TEST-03: Ranges keep both ends; covers one month, two months, accents, a dash and a range crossing the new year.
    /// <summary>The save flow compares the stated length with the planned days, so both ends must survive.</summary>
    [Theory]
    [InlineData("từ 3/11 đến 5/11", "2026-11-03", "2026-11-05")]
    [InlineData("3-5/11", "2026-11-03", "2026-11-05")]
    [InlineData("3 đến 5 tháng 11", "2026-11-03", "2026-11-05")]
    [InlineData("tu 30/11 toi 2/12", "2026-11-30", "2026-12-02")]
    [InlineData("30/12 - 2/1", "2026-12-30", "2027-01-02")]
    public void Read_Range_ReturnsBothEnds(string content, string start, string end)
    {
        var answer = AgentDatePolicy.Read(content, Today);

        answer!.Start.Should().Be(DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture));
        answer.End.Should().Be(DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture));
    }

    // TEST-04: Relative words and weekdays resolve from today; covers today, tomorrow, the day after, this week's next weekday, the same weekday and next week.
    /// <summary>Natural answers such as "thứ 7 tuần sau" need no calendar lookup by the user.</summary>
    [Theory]
    [InlineData("hôm nay", "2026-11-03")][InlineData("ngày mai", "2026-11-04")][InlineData("mai nhé", "2026-11-04")][InlineData("ngày kia", "2026-11-05")]
    [InlineData("thứ 7", "2026-11-07")][InlineData("thứ ba", "2026-11-03")][InlineData("thứ hai", "2026-11-09")][InlineData("chủ nhật", "2026-11-08")]
    [InlineData("thứ 7 tuần sau", "2026-11-14")][InlineData("chủ nhật tuần sau", "2026-11-15")][InlineData("đi thứ 4 tuần tới", "2026-11-11")]
    public void Read_RelativeWords_ResolveFromToday(string content, string expected) =>
        AgentDatePolicy.Read(content, Today)!.Start.Should().Be(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    // TEST-05: Messages that state no date return null; covers an edit naming a plan day, a restaurant called Mai, a bare number, empty text and an impossible date.
    /// <summary>"Đổi món ngày 2" must never be read as a date, and 31/2 must never silently become another day.</summary>
    [Theory]
    [InlineData("đổi món ngày 2")][InlineData("quán Mai có ngon không")][InlineData("5")][InlineData("ngày 5")][InlineData("")][InlineData("   ")][InlineData("31/2")][InlineData("13/13")]
    [InlineData("3/11 đến 31/2")]
    public void Read_NoUsableDate_ReturnsNull(string content) => AgentDatePolicy.Read(content, Today).Should().BeNull();

    // TEST-06: Attempts at a date are told apart from new requests; covers a bare number, an impossible date, an edit and plain text.
    /// <summary>The first gets asked again; the rest move on to normal handling instead of being stuck on the date question.</summary>
    [Theory]
    [InlineData("5", true)][InlineData("ngày 5 nhé", true)][InlineData("31/2", true)]
    [InlineData("đổi món ngày 2", false)][InlineData("đổi thành 4 ngày", false)][InlineData("thêm món chay", false)]
    public void LooksLikeDate_SeparatesDateAttemptsFromRequests(string content, bool expected) =>
        AgentDatePolicy.LooksLikeDate(content).Should().Be(expected);

    // TEST-07: Today follows Vietnam time around midnight; covers one tick before, exactly at and after 17:00 UTC.
    /// <summary>A traveler choosing "hôm nay" at 00:30 in Hanoi must get the new date although UTC is still the day before.</summary>
    [Theory]
    [InlineData("2026-11-03T16:59:59Z", 2026, 11, 3)]
    [InlineData("2026-11-03T17:00:00Z", 2026, 11, 4)]
    [InlineData("2026-11-03T17:00:01Z", 2026, 11, 4)]
    public void Today_UsesVietnamTime(string now, int year, int month, int day)
    {
        var clock = new FixedClock(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        AgentDatePolicy.Today(clock).Should().Be(new DateOnly(year, month, day));
    }

    /// <summary>Stops time at a chosen instant.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Returns the chosen instant.</summary>
        public override DateTimeOffset GetUtcNow() => now;
    }
}
