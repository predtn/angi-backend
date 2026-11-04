using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Agent;

/// <summary>Exercises every structured trip boundary before it can reach tools or the database.</summary>
public sealed class AgentStateValidatorTests
{
    // TEST-01: Reject invalid trip fields, monetary precision, lists, focus and coordinate combinations.
    /// <summary>Verifies all invalid structured-state equivalence classes.</summary>
    [Theory]
    [InlineData("schema")][InlineData("province-empty")][InlineData("province-long")][InlineData("days-zero")][InlineData("days-large")]
    [InlineData("title-null")][InlineData("title-empty")][InlineData("title-long")][InlineData("budget-negative")][InlineData("budget-fraction")]
    [InlineData("budget-large")][InlineData("price-negative")][InlineData("price-fraction")][InlineData("price-large")]
    [InlineData("tags-null")][InlineData("tags-zero")][InlineData("tags-duplicate")][InlineData("tags-large")]
    [InlineData("exclude-null")][InlineData("exclude-negative")][InlineData("exclude-duplicate")][InlineData("exclude-large")]
    [InlineData("origin-null")][InlineData("origin-invalid")][InlineData("latitude-low")][InlineData("latitude-high")]
    [InlineData("longitude-low")][InlineData("longitude-high")][InlineData("origin-half")][InlineData("pinned-empty")]
    [InlineData("none-coordinates")][InlineData("label-long")][InlineData("radius-low")][InlineData("radius-high")][InlineData("radius-no-origin")]
    [InlineData("note-long")][InlineData("question-long")][InlineData("focus-zero")][InlineData("focus-large")][InlineData("meal-invalid")]
    [InlineData("meal-without-day")][InlineData("date-overflow")]
    public void Validate_InvalidFields_Fails(string area)
    {
        var baseline = new AgentState { NumDays = 2 };
        var state = area switch
        {
            "schema" => baseline with { SchemaVersion = 2 }, "province-empty" => baseline with { Province = " " },
            "province-long" => baseline with { Province = new('x', 101) }, "days-zero" => baseline with { NumDays = 0 },
            "days-large" => baseline with { NumDays = 101 }, "title-null" => baseline with { Title = null! },
            "title-empty" => baseline with { Title = " " }, "title-long" => baseline with { Title = new('x', 201) },
            "budget-negative" => baseline with { BudgetAmount = -1 }, "budget-fraction" => baseline with { BudgetAmount = 0.5m },
            "budget-large" => baseline with { BudgetAmount = AgentStateValidator.MaxMoney + 1 },
            "price-negative" => baseline with { MaxPricePerMeal = -1 }, "price-fraction" => baseline with { MaxPricePerMeal = 0.5m },
            "price-large" => baseline with { MaxPricePerMeal = AgentStateValidator.MaxMoney + 1 },
            "tags-null" => baseline with { TagIds = null! }, "tags-zero" => baseline with { TagIds = [0] },
            "tags-duplicate" => baseline with { TagIds = [1, 1] }, "tags-large" => baseline with { TagIds = Enumerable.Range(1, 101).Select(x => (short)x).ToArray() },
            "exclude-null" => baseline with { ExcludeTagIds = null! }, "exclude-negative" => baseline with { ExcludeTagIds = [-1] },
            "exclude-duplicate" => baseline with { ExcludeTagIds = [1, 1] }, "exclude-large" => baseline with { ExcludeTagIds = Enumerable.Range(1, 101).Select(x => (short)x).ToArray() },
            "origin-null" => baseline with { OriginMode = null! }, "origin-invalid" => baseline with { OriginMode = "auto" },
            "latitude-low" => baseline with { OriginMode = "gps", OriginLatitude = -90.1m, OriginLongitude = 0 },
            "latitude-high" => baseline with { OriginMode = "gps", OriginLatitude = 90.1m, OriginLongitude = 0 },
            "longitude-low" => baseline with { OriginMode = "gps", OriginLatitude = 0, OriginLongitude = -180.1m },
            "longitude-high" => baseline with { OriginMode = "gps", OriginLatitude = 0, OriginLongitude = 180.1m },
            "origin-half" => baseline with { OriginMode = "gps", OriginLatitude = 0 }, "pinned-empty" => baseline with { OriginMode = "pinned" },
            "none-coordinates" => baseline with { OriginLatitude = 0, OriginLongitude = 0 }, "label-long" => baseline with { OriginLabel = new('x', 201) },
            "radius-low" => baseline with { RadiusKm = 0 }, "radius-high" => baseline with { RadiusKm = 51 }, "radius-no-origin" => baseline with { RadiusKm = 10 },
            "note-long" => baseline with { Note = new('x', 501) }, "question-long" => baseline with { PendingQuestion = new('x', 1001) },
            "focus-zero" => baseline with { FocusDay = 0 }, "focus-large" => baseline with { FocusDay = 3 },
            "meal-invalid" => baseline with { FocusDay = 1, FocusMeal = "brunch" }, "meal-without-day" => baseline with { FocusMeal = "lunch" },
            _ => baseline with { StartDate = DateOnly.MaxValue }
        };
        new AgentStateValidator().Validate(state).IsValid.Should().BeFalse(area);
    }

    // TEST-02: Accept inclusive trip, coordinate and monetary limits plus incomplete clarification state.
    /// <summary>Verifies legitimate minimum and maximum values remain usable.</summary>
    [Theory]
    [InlineData(1, -90, -180, 1)][InlineData(30, 90, 180, 50)]
    public void Validate_InclusiveBounds_Succeeds(short days, decimal latitude, decimal longitude, decimal radius)
    {
        var state = new AgentState { NumDays = days, Province = new('x', 100), Title = new('x', 200),
            BudgetAmount = AgentStateValidator.MaxMoney, MaxPricePerMeal = 0, OriginMode = "pinned", OriginLatitude = latitude,
            OriginLongitude = longitude, RadiusKm = radius, OriginLabel = new('x', 200), Note = new('x', 500),
            PendingQuestion = new('x', 1000), FocusDay = days, FocusMeal = "late_night" };
        new AgentStateValidator().Validate(state).IsValid.Should().BeTrue();
        new AgentStateValidator().Validate(new AgentState()).IsValid.Should().BeTrue();
    }

    // TEST-03: Day places must fit the trip and the map; covers a valid one, day 0, a day past the trip, duplicates, blank and long labels, bad coordinates and radius limits.
    /// <summary>Ensures a client or stored state can never carry an impossible day place into the search.</summary>
    [Theory]
    [InlineData(1, "Hoàn Kiếm", 21, 105, 3, true)]
    [InlineData(2, "Hoàn Kiếm", -90, -180, 1, true)]
    [InlineData(2, "Hoàn Kiếm", 90, 180, 50, true)]
    [InlineData(0, "Hoàn Kiếm", 21, 105, 3, false)]
    [InlineData(3, "Hoàn Kiếm", 21, 105, 3, false)]
    [InlineData(1, " ", 21, 105, 3, false)]
    [InlineData(1, "Hoàn Kiếm", 91, 105, 3, false)]
    [InlineData(1, "Hoàn Kiếm", 21, 181, 3, false)]
    [InlineData(1, "Hoàn Kiếm", 21, 105, 0, false)]
    [InlineData(1, "Hoàn Kiếm", 21, 105, 51, false)]
    public void Validate_DayAreas_FollowTripAndMapLimits(int day, string label, int latitude, int longitude, int radius, bool valid)
    {
        var state = new AgentState { NumDays = 2, DayAreas = [new((short)day, label, latitude, longitude, radius)] };

        new AgentStateValidator().Validate(state).IsValid.Should().Be(valid);
    }

    // TEST-04: One place per day, and a label longer than the limit is refused.
    /// <summary>Ensures duplicated days and oversized labels are rejected like every other backend-owned list.</summary>
    [Fact]
    public void Validate_DayAreas_RejectDuplicatesAndLongLabels()
    {
        var area = new AgentDayArea(1, "Hoàn Kiếm", 21m, 105m, 3m);

        new AgentStateValidator().Validate(new AgentState { NumDays = 2, DayAreas = [area, area] }).IsValid.Should().BeFalse();
        new AgentStateValidator().Validate(new AgentState { NumDays = 2, DayAreas = [area with { Label = new('x', 201) }] }).IsValid.Should().BeFalse();
        new AgentStateValidator().Validate(new AgentState { NumDays = 2, DayAreas = [area with { Label = new('x', 200) }] }).IsValid.Should().BeTrue();
    }
}
