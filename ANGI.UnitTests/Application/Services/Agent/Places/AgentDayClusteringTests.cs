using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Places;
using FluentAssertions;
using Option = ANGI.Application.Services.Agent.Places.AgentDayClustering.Option;

namespace ANGI.UnitTests.Application.Services.Agent.Places;

/// <summary>Checks that a day's meals stay close together and that a family dish name ("cà ri") matches its members.</summary>
public sealed class AgentDayClusteringTests
{
    // Hoàn Kiếm, Ba Đình (~2 km apart) and Hà Đông (~12 km south).
    private static readonly (double Lat, double Lng) HoanKiem = (21.0287, 105.8524), BaDinh = (21.0368, 105.8347), HaDong = (20.9715, 105.7788);

    private static Option At(int id, string name, (double Lat, double Lng) place) => new(id, name, place.Lat, place.Lng);

    // TEST-01: Lunch takes a lower-ranked dish near breakfast instead of the top dish across the city.
    /// <summary>Rank decides only among dishes within the leg distance.</summary>
    [Fact]
    public void Arrange_PrefersCloseDishOverTopRankedFarDish()
    {
        // STEP-01: Breakfast has one dish in Hoàn Kiếm; lunch ranks Hà Đông first and Ba Đình second.
        var lists = new Dictionary<string, Option[]>
        {
            ["breakfast"] = [At(1, "PHỞ", HoanKiem)],
            ["lunch"] = [At(2, "BÚN CHẢ", HaDong), At(3, "CƠM", BaDinh)]
        };

        // STEP-02: Arrange one day.
        var plan = AgentDayClustering.Arrange([1], ["breakfast", "lunch"], (_, meal) => lists[meal], 5, new HashSet<string>()).Single();

        // STEP-03: Assert lunch is the Ba Đình dish and the leg is about 2 km.
        plan.Meals.Select(pick => pick.Dish.DishId).Should().Equal(1, 3);
        plan.LegsKm.Single().Should().BeLessThan(3);
    }

    // TEST-02: With nothing within the leg distance, the nearest dish is taken rather than the top-ranked one.
    /// <summary>A far meal is never chosen when a nearer one exists.</summary>
    [Fact]
    public void Arrange_NothingClose_TakesNearest()
    {
        // STEP-01: Lunch candidates are all beyond 1 km of breakfast; Ba Đình is nearer than Hà Đông.
        var lists = new Dictionary<string, Option[]>
        {
            ["breakfast"] = [At(1, "PHỞ", HoanKiem)],
            ["lunch"] = [At(2, "BÚN CHẢ", HaDong), At(3, "CƠM", BaDinh)]
        };

        // STEP-02: Arrange with a 1 km leg.
        var plan = AgentDayClustering.Arrange([1], ["breakfast", "lunch"], (_, meal) => lists[meal], 1, new HashSet<string>()).Single();

        // STEP-03: Assert the nearer dish is chosen.
        plan.Meals[1].Dish.DishId.Should().Be(3);
    }

    // TEST-03: With the user's location in Hà Đông, breakfast starts there even though a Hoàn Kiếm dish ranks first.
    /// <summary>The day's walk starts where the user is.</summary>
    [Fact]
    public void Arrange_WithStartPoint_StartsNearTheUser()
    {
        // STEP-01: Breakfast ranks Hoàn Kiếm first and Hà Đông second; lunch has one dish in each place.
        var lists = new Dictionary<string, Option[]>
        {
            ["breakfast"] = [At(1, "PHỞ", HoanKiem), At(2, "BÁNH CUỐN", HaDong)],
            ["lunch"] = [At(3, "BÚN CHẢ", HoanKiem), At(4, "CƠM", HaDong)]
        };

        // STEP-02: Arrange one day starting from Hà Đông.
        var plan = AgentDayClustering.Arrange([1], ["breakfast", "lunch"], (_, meal) => lists[meal], 5, new HashSet<string>(), _ => HaDong).Single();

        // STEP-03: Assert both meals are the Hà Đông dishes and the first meal is at the start point.
        plan.Meals.Select(pick => pick.Dish.DishId).Should().Equal(2, 4);
        plan.StartKm.Should().Be(0);
    }

    // TEST-04: Two days with the same candidates do not repeat a dish name while another one is available.
    /// <summary>Names used on day 1 are avoided on day 2.</summary>
    [Fact]
    public void Arrange_TwoDays_DoNotRepeatNames()
    {
        // STEP-01: Each meal has two dishes in the same district.
        Option[] breakfast = [At(1, "PHỞ", HoanKiem), At(2, "BÁNH CUỐN", HoanKiem)];
        Option[] lunch = [At(3, "BÚN CHẢ", HoanKiem), At(4, "CƠM", HoanKiem)];

        // STEP-02: Arrange two days.
        var plans = AgentDayClustering.Arrange([1, 2], ["breakfast", "lunch"], (_, meal) => meal == "breakfast" ? breakfast : lunch, 5, new HashSet<string>());

        // STEP-03: Assert all four dishes are used once.
        plans.SelectMany(plan => plan.Meals).Select(pick => pick.Dish.DishId).Should().OnlyHaveUniqueItems().And.HaveCount(4);
    }

    // TEST-05: An edit of one meal lists dishes near the day's other meals first, keeping rank order among them.
    /// <summary>The far top dish moves behind the near ones.</summary>
    [Fact]
    public void NearNeighbours_PutsNearDishesFirst()
    {
        // STEP-01: Rank order is Hà Đông, Ba Đình, Hoàn Kiếm; the day's other meal is in Hoàn Kiếm.
        Option[] ranked = [At(1, "A", HaDong), At(2, "B", BaDinh), At(3, "C", HoanKiem)];

        // STEP-02: Order around the neighbour.
        var ordered = AgentDayClustering.NearNeighbours(ranked, option => (option.Latitude, option.Longitude), [HoanKiem], 5);

        // STEP-03: Assert the two near dishes come first in rank order, then the far one.
        ordered.Select(option => option.DishId).Should().Equal(2, 3, 1);
    }

    // TEST-06: A family name fits its members with or without accents; a different dish or a too-short word does not.
    /// <summary>"cà ri" stands for every curry dish in the catalog.</summary>
    [Theory]
    [InlineData("cà ri", "Cơm Cà Ri Nhật Gà Chiên", true)]
    [InlineData("ca ri", "Cà Ri Xanh Gà", true)]
    [InlineData("Cà Ri Chay Khoai Môn", "Cà Ri Chay Khoai Môn", true)]
    [InlineData("phở bò", "Phở Gà Bờ Hồ", false)]
    [InlineData("cá", "Cà Ri Xanh Gà", false)]
    public void FitsName_MatchesFamilyMembers(string requested, string dish, bool fits) =>
        AgentScopedPlanning.FitsName(requested, dish).Should().Be(fits);
}
