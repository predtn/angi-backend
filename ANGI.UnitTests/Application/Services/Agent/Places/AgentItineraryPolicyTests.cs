using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Places;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Places;

/// <summary>Verifies AgentPlaceResolver applies located places to the trip and tells the user how they were read.</summary>
public sealed class AgentItineraryPolicyTests
{
    // TEST-01: A new itinerary keeps other days, replaces restated days, clears days set to the whole province and grows the trip only when it must.
    /// <summary>Ensures later messages refine the itinerary rather than replacing all of it.</summary>
    [Fact]
    public void Apply_MergesDaysAndGrowsTripOnlyWhenNeeded()
    {
        // STEP-01: Create state as AgentState and get applied from AgentPlaceResolver.Apply.
        var state = new AgentState { Province = "Hà Nội", NumDays = 4, DayAreas = [Area(1, "A"), Area(2, "B"), Area(3, "C")] };

        var applied = AgentPlaceResolver.Apply(state, new("Hà Nội", [Area(2, "Mới")], null, [3], [], 3));

        // STEP-02: Assert applied.DayAreas.Select(filtered) equals ((short)1, "A"), applied.NumDays is 4 and AgentPlaceResolver.Apply() matches the condition.
        applied.DayAreas!.Select(area => (area.DayNumber, area.Label)).Should().Equal(((short)1, "A"), ((short)2, "Mới"));
        applied.NumDays.Should().Be(4);
        AgentPlaceResolver.Apply(new AgentState(), new("Hà Nội", [Area(3, "X")], null, [], [], 3)).Should().Match<AgentState>(x => x.NumDays == 3 && x.Province == "Hà Nội");
    }

    // TEST-02: Moving a day drops the wider radius found for its old place, other days keep theirs, and a new whole-trip area drops them all.
    /// <summary>Ensures a radius widened for one place never carries over to the next.</summary>
    [Fact]
    public void Apply_MovedDay_DropsItsWidenedMeals()
    {
        // STEP-01: Create state as AgentState.
        var state = new AgentState { Province = "Hà Nội", NumDays = 2, WidenedMeals = [new(1, "lunch", 8m), new(2, "lunch", 8m)] };

        // STEP-02: Assert AgentPlaceResolver.Apply().WidenedMeals equals a new AgentWidenedMeal and AgentPlaceResolver.Apply().WidenedMeals is null.
        AgentPlaceResolver.Apply(state, new("Hà Nội", [Area(1, "Mới")], null, [], [], 0)).WidenedMeals.Should().Equal(new AgentWidenedMeal(2, "lunch", 8m));
        AgentPlaceResolver.Apply(state, new("Hà Nội", [], new("Hà Nội", "Cầu Giấy", 21.03m, 105.79m, 3m), [], [], 0)).WidenedMeals.Should().BeNull();
    }

    // TEST-03: One area for the trip becomes a pinned origin with its radius, the same fields a roadmap keeps.
    /// <summary>Ensures a whole-trip area reuses the existing origin and radius path, including commit.</summary>
    [Fact]
    public void Apply_TripArea_BecomesPinnedOrigin()
    {
        // STEP-01: Get applied from AgentPlaceResolver.Apply.
        var applied = AgentPlaceResolver.Apply(new AgentState(), new("Hà Nội", [], new("Hà Nội", "Cầu Giấy", 21.03m, 105.79m, 3m), [], [], 0));

        // STEP-02: Assert applied matches the condition.
        applied.Should().Match<AgentState>(x => x.OriginMode == "pinned" && x.OriginLabel == "Cầu Giấy" && x.OriginLatitude == 21.03m && x.RadiusKm == 3m && x.Province == "Hà Nội" && x.NamedAreaLabel == "Cầu Giấy");
    }

    // TEST-04: The note states each day, a wide search, days left to the whole province and places not found; with no province it does not name one.
    /// <summary>Ensures the user sees how the message was read before dishes are chosen, including what could not be honoured.</summary>
    [Fact]
    public void Note_StatesEachDayAndWhatCouldNotBeFound()
    {
        // STEP-01: Create state as AgentState and get note from AgentPlaceResolver.Note.
        var state = new AgentState { Province = "Hà Nội", NumDays = 4, DayAreas = [Area(1, "Hoàn Kiếm"), Area(3, "Hòa Lạc") with { RadiusKm = 30m }] };

        var note = AgentPlaceResolver.Note(state, new("Hà Nội", state.DayAreas!, null, [], ["Đảo Xa"], 3));

        // STEP-02: Assert note contains the expected text, AgentPlaceResolver.Note() is the expected text and AgentPlaceResolver.Note() is null.
        note.Should().Contain("ngày 1 quanh Hoàn Kiếm").And.Contain("ngày 3 quanh Hòa Lạc (quanh đó còn ít quán").And.Contain("30 km")
            .And.Contain("“Đảo Xa”").And.Contain("toàn Hà Nội").And.Contain("Những ngày còn lại trong 4 ngày");
        AgentPlaceResolver.Note(new AgentState(), new(null, [], null, [], ["Đảo Xa"], 0)).Should().Be("Mình chưa tìm được quán quanh “Đảo Xa”.");
        AgentPlaceResolver.Note(new AgentState(), new(null, [], null, [], [], 0)).Should().BeNull();
    }

    // TEST-05: A known place is recognized without case or accents, from a day or the trip origin; other names are not.
    /// <summary>Ensures the catalog search guard only stops names the itinerary already holds.</summary>
    [Theory]
    [InlineData("hoàn kiếm", true)][InlineData("HOAN KIEM", true)][InlineData("Cầu Giấy", true)][InlineData("Phở Thìn", false)]
    public void IsKnownPlace_MatchesDayAndOriginLabels(string name, bool expected)
    {
        // STEP-01: Create state as AgentState.
        var state = new AgentState { DayAreas = [Area(1, "Hoàn Kiếm")], OriginLabel = "Cầu Giấy" };

        // STEP-02: Assert AgentPlaceResolver.IsKnownPlace() is expected.
        AgentPlaceResolver.IsKnownPlace(state, name).Should().Be(expected);
    }

    // TEST-06: A district is shown beside the place's name when known.
    /// <summary>Ensures a wrongly located place is visible in the first reply.</summary>
    [Fact]
    public void Describe_District_IsShown() =>
        // STEP-01: Assert AgentPlaceResolver.Describe() is the expected text.
        AgentPlaceResolver.Describe(Area(1, "Lăng Bác") with { District = "Ba Đình" }).Should().Be("ngày 1 quanh Lăng Bác (khu Ba Đình)");

    /// <summary>Builds a day area at a fixed Hà Nội point.</summary>
    private static AgentDayArea Area(short day, string label) => new(day, label, 21.03m, 105.85m, 3m);
}
