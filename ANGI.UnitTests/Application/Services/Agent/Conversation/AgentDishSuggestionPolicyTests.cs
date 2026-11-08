using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Verifies how the backend words a dish idea and fills the slots of the model's sentence.</summary>
public sealed class AgentDishSuggestionPolicyTests
{
    // TEST-01: Generic phrases are not catalog names, whereas a real dish name is.
    /// <summary>"một món ăn" used to reach the catalog and come back as "not found".</summary>
    [Theory]
    [InlineData("món ăn bất kì", true)]
    [InlineData("một món ăn", true)]
    [InlineData("Phở Gà", false)]
    [InlineData("", false)]
    public void IsGenericName_ReturnsExpected(string name, bool expected) =>
        // STEP-01: Assert AgentDishSuggestionPolicy.IsGenericName() is expected.
        AgentDishSuggestionPolicy.IsGenericName(name).Should().Be(expected);

    // TEST-02: One dish states its restaurant, price and address; several become a list; none gets the fixed line; a pin is mentioned.
    /// <summary>Every fact in the reply comes from the catalog entity.</summary>
    [Fact]
    public void Reply_StatesBackendFacts()
    {
        // STEP-01: Create dish as Dish and create other as Dish.
        var dish = new Dish { Name = "Bún Chả", Price = 45000, Restaurant = new Domain.Entities.Restaurant { Name = "Quán A", AddressLine = "1 Hàng Mành" } };
        var other = new Dish { Name = "Phở Gà", Price = 50000, Restaurant = new Domain.Entities.Restaurant { Name = "Quán B", AddressLine = "2 Lý Quốc Sư" } };

        // STEP-02: Assert AgentDishSuggestionPolicy.Reply() contains the expected text and AgentDishSuggestionPolicy.Reply() is NothingFound.
        AgentDishSuggestionPolicy.Reply([dish], aroundPin: false).Should().Contain("Bún Chả ở Quán A").And.Contain("45.000 đồng").And.Contain("1 Hàng Mành").And.NotContain("bản đồ");
        AgentDishSuggestionPolicy.Reply([dish], aroundPin: true).Should().Contain("quanh điểm bạn đã chấm trên bản đồ");
        AgentDishSuggestionPolicy.Reply([dish, other], aroundPin: false).Should().Contain("- Bún Chả ở Quán A").And.Contain("- Phở Gà ở Quán B");
        AgentDishSuggestionPolicy.Reply([], aroundPin: false).Should().Be(AgentDishSuggestionPolicy.NothingFound);
        // STEP-03: Assert the taste opening appears only when ranked by taste, for one dish and for several.
        AgentDishSuggestionPolicy.Reply([dish], aroundPin: false, personalized: true).Should().StartWith("Theo khẩu vị của bạn, mình gợi ý Bún Chả");
        AgentDishSuggestionPolicy.Reply([dish, other], aroundPin: false, personalized: true).Should().StartWith("Theo khẩu vị của bạn, mình gợi ý vài món");
        AgentDishSuggestionPolicy.Reply([], aroundPin: false, personalized: true).Should().Be(AgentDishSuggestionPolicy.NothingFound);
    }

    // TEST-03: The packed suggestion carries each dish's facts, the ranking, the pin and the same fixed reply as before; a dish with no address has none, and no dish gives the fixed line.
    /// <summary>The model and the fixed reply must state the same facts.</summary>
    [Fact]
    public void Suggestion_CarriesFactsAndFixedReply()
    {
        // STEP-01: Create dish as Dish and bare as Dish.
        var dish = new Dish { Name = "Bún Chả", Price = 45000, Restaurant = new Domain.Entities.Restaurant { Name = "Quán A", AddressLine = "1 Hàng Mành" } };
        var bare = new Dish { Name = "Phở Gà", Price = 50000, Restaurant = new Domain.Entities.Restaurant { Name = "Quán B", AddressLine = " " } };

        // STEP-02: Prepare suggestion through AgentDishSuggestionPolicy.Suggestion.
        var suggestion = AgentDishSuggestionPolicy.Suggestion([dish, bare], aroundPin: true, personalized: true);

        // STEP-03: Assert the dishes, flags and fixed replies.
        suggestion.Dishes.Should().Equal(new AgentSuggestedDish("Bún Chả", "Quán A", 45000, "1 Hàng Mành"), new AgentSuggestedDish("Phở Gà", "Quán B", 50000, null));
        suggestion.Personalized.Should().BeTrue();
        suggestion.AroundPin.Should().BeTrue();
        suggestion.FixedReply.Should().Be(AgentDishSuggestionPolicy.Reply([dish, bare], aroundPin: true, personalized: true));
        AgentDishSuggestionPolicy.Suggestion([], false, false).FixedReply.Should().Be(AgentDishSuggestionPolicy.NothingFound);
    }

    // TEST-04: The fixed reply invites adding the dish only over a plan, in one form for one dish and for several, and the packed suggestion carries the same flag.
    /// <summary>Keeps the fallback as useful as the model's wording.</summary>
    [Fact]
    public void Reply_OverPlan_InvitesAdding()
    {
        // STEP-01: Create dish as Dish.
        var dish = new Dish { Name = "Bún Chả", Price = 45000, Restaurant = new Domain.Entities.Restaurant { Name = "Quán A" } };
        const string invitation = "Muốn cho vào lịch thì nhắn mình ngày và bữa nhé.";

        // STEP-02: Assert the replies and the packed flag.
        AgentDishSuggestionPolicy.Reply([dish], false, hasPlan: true).Should().EndWith(invitation);
        AgentDishSuggestionPolicy.Reply([dish, dish], false, hasPlan: true).Should().EndWith(invitation);
        AgentDishSuggestionPolicy.Reply([dish], false).Should().NotContain(invitation);
        AgentDishSuggestionPolicy.Reply([], false, hasPlan: true).Should().Be(AgentDishSuggestionPolicy.NothingFound);
        AgentDishSuggestionPolicy.Suggestion([dish], false, false, hasPlan: true).HasPlan.Should().BeTrue();
    }

    // TEST-05: The fixed reply names the place searched and the place not found, states meals unless a meal was asked for, and gives every dish its address; the packed facts match.
    /// <summary>The fallback must not be less clear than the model's wording.</summary>
    [Fact]
    public void Reply_StatesAreaMealsAndAddress()
    {
        // STEP-01: Create breakfast as a dish tagged for one meal, and other as a dish tagged for every main meal.
        static DishTag Meal(string code) => new() { Tag = new Tag { Code = code, TagType = TagType.MealTime } };
        var breakfast = new Dish { Name = "Bún Chả", Price = 45000, Restaurant = new Domain.Entities.Restaurant { Name = "Quán A", AddressLine = "1 Hàng Mành" }, DishTags = [Meal("breakfast")] };
        var anyMeal = new Dish { Name = "Phở Gà", Price = 50000, Restaurant = new Domain.Entities.Restaurant { Name = "Quán B", AddressLine = "2 Lý Quốc Sư" }, DishTags = [Meal("breakfast"), Meal("lunch"), Meal("dinner")] };

        // STEP-02: Assert the wording for an area, a place not found, meals and addresses.
        AgentDishSuggestionPolicy.Reply([breakfast], false, area: "Cầu Giấy").Should().Contain("Bún Chả (hợp bữa sáng) ở Quán A quanh Cầu Giấy").And.Contain("Địa chỉ: 1 Hàng Mành");
        AgentDishSuggestionPolicy.Reply([breakfast], false, areaNotFound: "Xóm Núi").Should().StartWith("Mình chưa định vị được “Xóm Núi”");
        AgentDishSuggestionPolicy.Reply([breakfast], false, mealAsked: true).Should().NotContain("hợp bữa");
        AgentDishSuggestionPolicy.Reply([breakfast, anyMeal], false).Should().Contain("Địa chỉ: 1 Hàng Mành").And.Contain("Địa chỉ: 2 Lý Quốc Sư").And.Contain("- Phở Gà ở Quán B");

        // STEP-03: Assert the packed facts carry the same meals, area and missing place.
        var packed = AgentDishSuggestionPolicy.Suggestion([breakfast, anyMeal], false, false, area: "Cầu Giấy", areaNotFound: null);
        packed.Dishes[0].Meals.Should().Be("bữa sáng");
        packed.Dishes[1].Meals.Should().BeNull();
        packed.Area.Should().Be("Cầu Giấy");
        AgentDishSuggestionPolicy.Suggestion([breakfast], false, false, mealAsked: true).Dishes[0].Meals.Should().BeNull();
    }

    private static readonly AgentDishSuggestion One = new([new("Bún Chả", "Quán A", 45000, "1 Hàng Mành")], false, false, "Mình gợi ý Bún Chả.");
    private static readonly AgentDishSuggestion Two = new([new("Bún Chả", "Quán A", 45000, null), new("Phở Gà", "Quán B", 50000, null)], false, false, "Mình gợi ý vài món.");

    /// <summary>Builds a turn whose only trusted facts are the suggestion's own.</summary>
    private static AgentContext Context(AgentDishSuggestion suggestion)
    {
        var context = AgentTestData.Context();
        context.Request.Content = "gợi ý món ăn";
        context.State = new AgentState();
        context.DishSuggestion = suggestion;
        return context;
    }
}
