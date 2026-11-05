using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Places;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Places;

/// <summary>Verifies AgentItineraryPolicy reads day-by-day places from the user's wording and never mistakes food requests for places.</summary>
public sealed class AgentItineraryPolicyTests
{
    // TEST-01: The itinerary wordings the user reported are split into one place per day; covers line breaks, colons, lower case and a one-line list.
    /// <summary>Ensures the exact messages of the failed test conversation produce the right day and place.</summary>
    [Theory]
    [InlineData("Ngày 1 tôi ở Hoàn kiếm\nNgày 2 tôi đi Lăng Bác\nNgày 3 tôi ở Hoàng Mai\nHãy Lập lịch đi ăn", "Hoàn kiếm", "Lăng Bác", "Hoàng Mai")]
    [InlineData("Hiện tại tôi có hành trình như sau \nNgày 1 : Tôi tham quan Hoàn kiếm \nngày 2 : Tôi tham quan lăng bác \nNgày 3 : Tôi ở Hòa lạc \nHãy lập lịch theo hành trình của tôi", "Hoàn kiếm", "lăng bác", "Hòa lạc")]
    [InlineData("Ngày 1 Hoàn Kiếm, ngày 2 Lăng Bác, ngày 3 Hoàng Mai", "Hoàn Kiếm", "Lăng Bác", "Hoàng Mai")]
    [InlineData("ngay 1 o Hoan Kiem. ngay 2 di Lang Bac. ngay 3 den Hoang Mai", "Hoan Kiem", "Lang Bac", "Hoang Mai")]
    public void Stops_ReportedItineraries_ReadsOnePlacePerDay(string content, string first, string second, string third)
    {
        // STEP-01: Get stops from AgentItineraryPolicy.Stops.
        var stops = AgentItineraryPolicy.Stops(content);

        // STEP-02: Assert stops.Select(filtered) equals first, stops.Select(filtered) equals 1 and stops only holds items matching the condition.
        stops.Select(stop => stop.Place).Should().Equal(first, second, third);
        stops.Select(stop => stop.Days.Single()).Should().Equal((short)1, (short)2, (short)3);
        stops.Should().OnlyContain(stop => stop.Explicit);
    }

    // TEST-02: One place can cover several days; covers a dash, "và", "đến" with a repeated day word, and a reversed range.
    /// <summary>Ensures a range expands to every day it names, and a nonsense range names none.</summary>
    [Theory]
    [InlineData("ngày 1-2 ở Cầu Giấy", new short[] { 1, 2 })]
    [InlineData("ngày 1 và 3 ở Tây Hồ", new short[] { 1, 3 })]
    [InlineData("Ngày 1 đến ngày 3 ở Ba Đình", new short[] { 1, 2, 3 })]
    [InlineData("ngày 2 tới 4 ở Long Biên", new short[] { 2, 3, 4 })]
    public void Stops_DayRange_ExpandsToEveryDay(string content, short[] days) =>
        // STEP-01: Assert AgentItineraryPolicy.Stops().Single().Days equals days.
        AgentItineraryPolicy.Stops(content).Single().Days.Should().Equal(days);

    // TEST-03: Food, taste, meals, budgets, dates, restaurants and day 0 or 31 are not stops; these are the wordings the normal planning flow must keep reading.
    /// <summary>Ensures existing day-scoped preferences never turn into a place search.</summary>
    [Theory]
    [InlineData("ngày 1 ăn hải sản")][InlineData("ngày 2 chỉ đồ uống")][InlineData("ngày 3 theo bạn quyết định")][InlineData("ngày 1 cay nhẹ")]
    [InlineData("bữa tối ngày 1 ở Hoàn Kiếm")][InlineData("3 ngày 1 triệu")][InlineData("500k 1 ngày 2 bữa")][InlineData("ngày 15/11 ở Hà Nội")]
    [InlineData("ngày 1 ở quán Phở Thìn")][InlineData("ngày 0 ở Cầu Giấy")][InlineData("ngày 101 ở Cầu Giấy")][InlineData("lập lịch cho tôi")]
    // STEP-01: Assert AgentItineraryPolicy.Stops() is empty.
    public void Stops_FoodBudgetOrDate_IsNotAPlace(string content) => AgentItineraryPolicy.Stops(content).Should().BeEmpty();

    // TEST-04: Words after the place are dropped; covers food, a closing word, money and a district written with its prefix.
    /// <summary>Ensures the label is only the place, so address matching and the reply stay clean.</summary>
    [Theory]
    [InlineData("Ngày 1 ở Hoàn Kiếm ăn phở", "Hoàn Kiếm")]
    [InlineData("Ngày 1 ở Cầu Giấy nhé", "Cầu Giấy")]
    [InlineData("Ngày 1 ở Tây Hồ 500k", "Tây Hồ")]
    [InlineData("Ngày 1 tôi ở quận Hoàn Kiếm", "Hoàn Kiếm")]
    [InlineData("Ngày 1 gần phố Huế", "phố Huế")]
    public void Stops_TextAroundPlace_KeepsOnlyThePlace(string content, string place) =>
        // STEP-01: Assert AgentItineraryPolicy.Stops().Single().Place is place.
        AgentItineraryPolicy.Stops(content).Single().Place.Should().Be(place);

    // TEST-05: Only a going word, a colon or a list marks a stop as meant to be a place; a bare single name is not explicit.
    /// <summary>Ensures an unknown bare phrase is not reported to the user as an unknown place.</summary>
    [Theory]
    [InlineData("ngày 1 Cầu Giấy", false)]
    [InlineData("ngày 1: Cầu Giấy", true)]
    [InlineData("ngày 1 tôi đi Cầu Giấy", true)]
    [InlineData("ngày 1 Cầu Giấy, ngày 2 Tây Hồ", true)]
    public void Stops_ExplicitOnlyWithGoingWordColonOrList(string content, bool expected) =>
        // STEP-01: Assert AgentItineraryPolicy.Stops()[0].Explicit is expected.
        AgentItineraryPolicy.Stops(content)[0].Explicit.Should().Be(expected);

    // TEST-06: One area for the whole trip; covers an "ở" phrase, money after it, a bare answer only when asked, and vague, restaurant or too long replies.
    /// <summary>Ensures "Vậy thì ở Cầu Giấy" and a bare "Hai bà trưng" are read as areas while "gần tôi" is left to the location flow.</summary>
    [Theory]
    [InlineData("Vậy thì ở Cầu Giấy", false, "Cầu Giấy")]
    [InlineData("lập lịch 3 ngày ở Tây Hồ nhé", false, "Tây Hồ")]
    [InlineData("ở Hà Nội 3 ngày", false, "Hà Nội")]
    [InlineData("Hai bà trưng", true, "Hai bà trưng")]
    [InlineData("Hai bà trưng", false, null)]
    [InlineData("ở đây", false, null)]
    [InlineData("gần tôi", false, null)]
    [InlineData("ăn ở quán phở", false, null)]
    [InlineData("mình muốn hỏi thêm một vài điều nữa về chuyến đi", true, null)]
    public void TripArea_ReadsOnlyARealArea(string content, bool asked, string? expected) =>
        // STEP-01: Assert AgentItineraryPolicy.TripArea() is expected.
        AgentItineraryPolicy.TripArea(content, asked).Should().Be(expected);

    // TEST-07: A new itinerary keeps other days, replaces restated days, clears days set to the whole province and grows the trip only when it must.
    /// <summary>Ensures later messages refine the itinerary rather than replacing all of it.</summary>
    [Fact]
    public void Apply_MergesDaysAndGrowsTripOnlyWhenNeeded()
    {
        // STEP-01: Create state as AgentState and get applied from AgentItineraryPolicy.Apply.
        var state = new AgentState { Province = "Hà Nội", NumDays = 4, DayAreas = [Area(1, "A"), Area(2, "B"), Area(3, "C")] };

        var applied = AgentItineraryPolicy.Apply(state, new("Hà Nội", [Area(2, "Mới")], null, [3], [], 3));

        // STEP-02: Assert applied.DayAreas.Select(filtered) equals ((short)1, "A"), applied.NumDays is 4 and AgentItineraryPolicy.Apply() matches the condition.
        applied.DayAreas!.Select(area => (area.DayNumber, area.Label)).Should().Equal(((short)1, "A"), ((short)2, "Mới"));
        applied.NumDays.Should().Be(4);
        AgentItineraryPolicy.Apply(new AgentState(), new("Hà Nội", [Area(3, "X")], null, [], [], 3)).Should().Match<AgentState>(x => x.NumDays == 3 && x.Province == "Hà Nội");
    }

    // TEST-08: Moving a day drops the wider radius found for its old place, other days keep theirs, and a new whole-trip area drops them all.
    /// <summary>Ensures a radius widened for one place never carries over to the next.</summary>
    [Fact]
    public void Apply_MovedDay_DropsItsWidenedMeals()
    {
        // STEP-01: Create state as AgentState.
        var state = new AgentState { Province = "Hà Nội", NumDays = 2, WidenedMeals = [new(1, "lunch", 8m), new(2, "lunch", 8m)] };

        // STEP-02: Assert AgentItineraryPolicy.Apply().WidenedMeals equals a new AgentWidenedMeal and AgentItineraryPolicy.Apply().WidenedMeals is null.
        AgentItineraryPolicy.Apply(state, new("Hà Nội", [Area(1, "Mới")], null, [], [], 0)).WidenedMeals.Should().Equal(new AgentWidenedMeal(2, "lunch", 8m));
        AgentItineraryPolicy.Apply(state, new("Hà Nội", [], new("Hà Nội", "Cầu Giấy", 21.03m, 105.79m, 3m), [], [], 0)).WidenedMeals.Should().BeNull();
    }

    // TEST-09: One area for the trip becomes a pinned origin with its radius, the same fields a roadmap keeps.
    /// <summary>Ensures a whole-trip area reuses the existing origin and radius path, including commit.</summary>
    [Fact]
    public void Apply_TripArea_BecomesPinnedOrigin()
    {
        // STEP-01: Get applied from AgentItineraryPolicy.Apply.
        var applied = AgentItineraryPolicy.Apply(new AgentState(), new("Hà Nội", [], new("Hà Nội", "Cầu Giấy", 21.03m, 105.79m, 3m), [], [], 0));

        // STEP-02: Assert applied matches the condition.
        applied.Should().Match<AgentState>(x => x.OriginMode == "pinned" && x.OriginLabel == "Cầu Giấy" && x.OriginLatitude == 21.03m && x.RadiusKm == 3m && x.Province == "Hà Nội" && x.NamedAreaLabel == "Cầu Giấy");
    }

    // TEST-10: The note states each day, a wide search, days left to the whole province and places not found; with no province it does not name one.
    /// <summary>Ensures the user sees how the message was read before dishes are chosen, including what could not be honoured.</summary>
    [Fact]
    public void Note_StatesEachDayAndWhatCouldNotBeFound()
    {
        // STEP-01: Create state as AgentState and get note from AgentItineraryPolicy.Note.
        var state = new AgentState { Province = "Hà Nội", NumDays = 4, DayAreas = [Area(1, "Hoàn Kiếm"), Area(3, "Hòa Lạc") with { RadiusKm = 30m }] };

        var note = AgentItineraryPolicy.Note(state, new("Hà Nội", state.DayAreas!, null, [], ["Đảo Xa"], 3));

        // STEP-02: Assert note contains the expected text, AgentItineraryPolicy.Note() is the expected text and AgentItineraryPolicy.Note() is null.
        note.Should().Contain("ngày 1 quanh Hoàn Kiếm").And.Contain("ngày 3 quanh Hòa Lạc (quanh đó còn ít quán").And.Contain("30 km")
            .And.Contain("“Đảo Xa”").And.Contain("toàn Hà Nội").And.Contain("Những ngày còn lại trong 4 ngày");
        AgentItineraryPolicy.Note(new AgentState(), new(null, [], null, [], ["Đảo Xa"], 0)).Should().Be("Mình chưa tìm được quán quanh “Đảo Xa”.");
        AgentItineraryPolicy.Note(new AgentState(), new(null, [], null, [], [], 0)).Should().BeNull();
    }

    // TEST-11: A known place is recognized without case or accents, from a day or the trip origin; other names are not.
    /// <summary>Ensures the catalog search guard only stops names the itinerary already holds.</summary>
    [Theory]
    [InlineData("hoàn kiếm", true)][InlineData("HOAN KIEM", true)][InlineData("Cầu Giấy", true)][InlineData("Phở Thìn", false)]
    public void IsKnownPlace_MatchesDayAndOriginLabels(string name, bool expected)
    {
        // STEP-01: Create state as AgentState.
        var state = new AgentState { DayAreas = [Area(1, "Hoàn Kiếm")], OriginLabel = "Cầu Giấy" };

        // STEP-02: Assert AgentItineraryPolicy.IsKnownPlace() is expected.
        AgentItineraryPolicy.IsKnownPlace(state, name).Should().Be(expected);
    }

    // TEST-12: A message that only names a place is read with or without "đổi"; only clear place wording may use the map; questions, dishes, restaurants and day wording are not bare areas.
    /// <summary>Ensures the "which day?" question only fires on the short wording the user described, never on an ordinary edit.</summary>
    [Theory]
    [InlineData("đổi sang khu Cầu Giấy", "Cầu Giấy", true)]
    [InlineData("đổi hành trình sang Hoàn Kiếm nhé", "Hoàn Kiếm", true)]
    [InlineData("đổi sang Cầu Giấy đi", "Cầu Giấy", false)]
    [InlineData("tôi muốn chuyển qua Tây Hồ", "Tây Hồ", false)]
    [InlineData("ở Hoàn Kiếm thôi", "Hoàn Kiếm", true)]
    [InlineData("tôi đi Lăng Bác", "Lăng Bác", true)]
    [InlineData("đi ăn ở Tây Hồ", "Tây Hồ", true)]
    [InlineData("khu Cầu Giấy.", "Cầu Giấy", true)]
    [InlineData("đổi sang món khác", null, false)]
    [InlineData("đổi sang quán Phở Thìn", null, false)]
    [InlineData("ở đâu", null, false)]
    [InlineData("đi ăn phở", null, false)]
    [InlineData("tìm phở ở Cầu Giấy", null, false)]
    [InlineData("có quán nào ở Hoàn Kiếm không", null, false)]
    [InlineData("đổi bữa trưa ngày 1 sang món khác", null, false)]
    [InlineData("xin chào", null, false)]
    public void BareArea_OnlyWhenTheMessageJustNamesAPlace(string content, string? name, bool searchable)
    {
        // STEP-01: Get area from AgentItineraryPolicy.BareArea.
        var area = AgentItineraryPolicy.BareArea(content);

        // STEP-02: Assert .Name is name, depending on name is null, area is null and area.Value.Searchable is searchable.
        area?.Name.Should().Be(name);
        if (name is null) area.Should().BeNull();
        else area!.Value.Searchable.Should().Be(searchable);
    }

    // TEST-13: "ngày nào?" accepts one day, several, a bare number and the whole trip; days past the trip are dropped, and a reply about something else is not an answer.
    /// <summary>Ensures "ngày 2 ăn phở" is not swallowed as an answer, and that the user is never stuck on the question.</summary>
    [Theory]
    [InlineData("ngày 2", new short[] { 2 })]
    [InlineData("Ngày 2 nhé.", new short[] { 2 })]
    [InlineData("đổi ngày 3 thôi", new short[] { 3 })]
    [InlineData("ngày 1 và 3", new short[] { 1, 3 })]
    [InlineData("ngày 1, ngày 2", new short[] { 1, 2 })]
    [InlineData("ngày 2 đến ngày 4", new short[] { 2, 3, 4 })]
    [InlineData("2", new short[] { 2 })]
    [InlineData("1 và 2", new short[] { 1, 2 })]
    [InlineData("cả chuyến", new short[] { 1, 2, 3, 4 })]
    [InlineData("tất cả nhé", new short[] { 1, 2, 3, 4 })]
    [InlineData("cả 4 ngày", new short[] { 1, 2, 3, 4 })]
    [InlineData("ngày 9", new short[0])]
    [InlineData("ngày 2 ăn phở", new short[0])]
    [InlineData("không", new short[0])]
    [InlineData("chốt lịch", new short[0])]
    public void AnsweredDays_ReadsOnlyAnAnswer(string content, short[] expected) =>
        // STEP-01: Assert AgentItineraryPolicy.AnsweredDays() equals expected.
        AgentItineraryPolicy.AnsweredDays(content, 4).Should().Equal(expected);

    // TEST-14: A day can be moved with the change verb before or after it; a dish name in the same wording stays out because only explicit stops may use the map.
    /// <summary>Ensures "đổi ngày 2 sang Cầu Giấy" is a stop while "ngày 2 đổi sang bún chả" cannot reach the map as a place.</summary>
    [Theory]
    [InlineData("đổi ngày 2 sang Cầu Giấy", "Cầu Giấy")]
    [InlineData("ngày 2 đổi sang Cầu Giấy", "Cầu Giấy")]
    [InlineData("ngày 2 chuyển sang khu Tây Hồ", "Tây Hồ")]
    public void Stops_ChangeWording_ReadsThePlace(string content, string place) =>
        // STEP-01: Assert AgentItineraryPolicy.Stops().Single().Place is place.
        AgentItineraryPolicy.Stops(content).Single().Place.Should().Be(place);

    // TEST-15: The change verb alone never allows a map search for the place.
    /// <summary>Protects "ngày 2 đổi sang bún chả" from matching a restaurant on the map, while "khu" still counts as clearly a place.</summary>
    [Theory]
    [InlineData("ngày 2 đổi sang bún chả", false)]
    [InlineData("ngày 2 đổi sang khu Tây Hồ", true)]
    public void Stops_ChangeWording_ExplicitOnlyWithPlaceWord(string content, bool expected) =>
        // STEP-01: Assert AgentItineraryPolicy.Stops().Single().Explicit is expected.
        AgentItineraryPolicy.Stops(content).Single().Explicit.Should().Be(expected);

    // TEST-16: With accents the words after a place are matched on accented text, so names ending in a look-alike stay whole; without accents the old matching applies.
    /// <summary>Protects "Hội An", "Hàng Mã" and "Chợ Mơ", which accent-free matching would cut into "Hội", "Hàng" and "Chợ".</summary>
    [Theory]
    [InlineData("Ngày 1 đi Hội An", "Hội An")]
    [InlineData("Ngày 1 đi Hàng Mã nhé", "Hàng Mã")]
    [InlineData("Ngày 1 đi Chợ Mơ cho tôi", "Chợ Mơ")]
    [InlineData("Ngày 1 đi Hồ Gươm ở Hoàn Kiếm", "Hồ Gươm")]
    [InlineData("ngay 1 o hoan kiem an pho", "hoan kiem")]
    public void Stops_NameEndingLikeAFiller_StaysWhole(string content, string place) =>
        // STEP-01: Assert AgentItineraryPolicy.Stops().Single().Place is place.
        AgentItineraryPolicy.Stops(content).Single().Place.Should().Be(place);

    // TEST-17: A complaint names its day and the right place; covers both messages of conversation 121, a bare complaint, one with no day, and a day found by its label.
    /// <summary>Ensures the place the user wants is read, and the place they are complaining about is not.</summary>
    [Theory]
    [InlineData("Ngày 1 đang đề xuất sai, tôi đi Hồ Gươm nhưng sao roadmap lại chỉ sang Hà Đông nhỉ", new short[] { 1 }, "Hồ Gươm")]
    [InlineData("ý tôi là Hồ Gươm ở Hoàn Kiếm mà sao lại đề xuất ở Hà Đông", new short[] { 1 }, "Hồ Gươm")]
    [InlineData("ngày 1 sai", new short[] { 1 }, null)]
    [InlineData("ngày 1 và 2 sai rồi", new short[] { 1, 2 }, null)]
    [InlineData("sao lại đề xuất ở Hà Đông", new short[0], null)]
    [InlineData("nhầm rồi, phải là Cầu Giấy", new short[0], "Cầu Giấy")]
    public void Correction_ReadsDayAndRightPlace(string content, short[] days, string? place)
    {
        // STEP-01: Derive correction from AgentItineraryPolicy.Correction().
        var correction = AgentItineraryPolicy.Correction(content, new AgentState { NumDays = 3, DayAreas = [Area(1, "Hồ Gươm")] })!;

        // STEP-02: Assert correction.Days equals days and correction.Place is place.
        correction.Days.Should().Equal(days);
        correction.Place.Should().Be(place);
    }

    // TEST-18: Wording about a dish or restaurant, a city whose accent-free name contains "sai", ordinary edits and a plan without places are not complaints about a place.
    /// <summary>Guards the existing edit flow from the new correction case.</summary>
    [Theory]
    [InlineData("bữa trưa ngày 1 sai vị", true)]
    [InlineData("món này sai rồi", true)]
    [InlineData("quán này sai rồi", true)]
    [InlineData("ngày 2 đi Sài Gòn", true)]
    [InlineData("đổi ngày 1 sang Cầu Giấy", true)]
    [InlineData("ngày 1 sai", false)]
    public void Correction_NotAboutAPlace_ReturnsNull(string content, bool withPlaces)
    {
        // STEP-01: Create state as AgentState.
        var state = new AgentState { NumDays = 3, DayAreas = withPlaces ? [Area(1, "Hồ Gươm")] : null };

        // STEP-02: Assert AgentItineraryPolicy.Correction() is null.
        AgentItineraryPolicy.Correction(content, state).Should().BeNull();
    }

    // TEST-19: A district is shown beside the place's name when known.
    /// <summary>Ensures a wrongly located place is visible in the first reply.</summary>
    [Fact]
    public void Describe_District_IsShown() =>
        // STEP-01: Assert AgentItineraryPolicy.Describe() is the expected text.
        AgentItineraryPolicy.Describe(Area(1, "Lăng Bác") with { District = "Ba Đình" }).Should().Be("ngày 1 quanh Lăng Bác (khu Ba Đình)");

    /// <summary>Builds a day area at a fixed Hà Nội point.</summary>
    private static AgentDayArea Area(short day, string label) => new(day, label, 21.03m, 105.85m, 3m);
}
