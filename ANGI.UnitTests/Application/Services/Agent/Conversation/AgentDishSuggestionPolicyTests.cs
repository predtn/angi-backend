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

    // TEST-03: The model's sentence is kept and the backend fills its slots; covers any order within a dish, an address, and a unit the model added to the price.
    /// <summary>The wording stays the model's own while the facts can only be the backend's.</summary>
    [Theory]
    [InlineData("Bạn thử {name1} ở {restaurant1} nhé, khoảng {price1} thôi!", "Bạn thử Bún Chả ở Quán A (1 Hàng Mành) nhé, khoảng 45.000 đồng thôi!")]
    [InlineData("Ghé {restaurant1} làm một phần {name1} đi bạn, tầm {price1}.", "Ghé Quán A (1 Hàng Mành) làm một phần Bún Chả đi bạn, tầm 45.000 đồng.")]
    [InlineData("{name1} ở {restaurant1} giá {price1}, địa chỉ {address1} nha.", "Bún Chả ở Quán A giá 45.000 đồng, địa chỉ 1 Hàng Mành nha.")]
    [InlineData("Thử {name1} ở {restaurant1}, {price1} đồng thôi nè!", "Thử Bún Chả ở Quán A (1 Hàng Mành), 45.000 đồng thôi nè!")]
    public void Worded_SlotsFit_AreFilled(string reply, string shown) =>
        // STEP-01: Assert AgentDishSuggestionPolicy.Worded() is shown.
        AgentDishSuggestionPolicy.Worded(reply, Context(One), One).Should().Be(shown);

    // TEST-04: A reply that misses or invents something shows the fixed reply; covers each missing slot, an unknown slot, a dish that does not exist, a written name and price, a claim of fame, code, nothing and only slots.
    /// <summary>A false fact about where to eat is worse than a plainer sentence.</summary>
    [Theory]
    [InlineData("Bạn thử món ở {restaurant1}, {price1} nhé!")]
    [InlineData("Bạn thử {name1}, {price1} nhé!")]
    [InlineData("Bạn thử {name1} ở {restaurant1} nhé!")]
    [InlineData("Bạn thử {name1} ở {restaurant1}, {price1}, {rating1} nhé!")]
    [InlineData("Bạn thử {name1} ở {restaurant1}, {price1}, hoặc {name2} nhé!")]
    [InlineData("Bạn thử {name0} ở {restaurant1}, {price1} nhé!")]
    [InlineData("Bạn thử Bún Chả ở {restaurant1}, {name1}, {price1} nhé!")]
    [InlineData("Bạn thử {name1} ở {restaurant1}, {price1}, 45.000 đồng thôi nhé!")]
    [InlineData("Bạn thử {name1} ở {restaurant1} nổi tiếng nhé, {price1}!")]
    [InlineData("print(\"{name1} ở {restaurant1}, {price1}\")")]
    [InlineData("{name1} {restaurant1} {price1}")]
    [InlineData("")]
    [InlineData(null)]
    public void Worded_SlotsMissingOrFactInvented_ShowsFixedReply(string? reply) =>
        // STEP-01: Assert AgentDishSuggestionPolicy.Worded() is One.FixedReply.
        AgentDishSuggestionPolicy.Worded(reply, Context(One), One).Should().Be(One.FixedReply);

    // TEST-05: The longest allowed wording around the slots is shown and one character more is not.
    /// <summary>Keeps a runaway reply out without blocking a full one.</summary>
    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void Worded_LengthBoundary_DecidesFallback(int length, bool shown)
    {
        // STEP-01: Create words as the text around the slots, padded to length.
        const string start = "Bạn thử ở nhé";
        var words = start + new string('!', length - start.Length);

        // STEP-02: Prepare result through AgentDishSuggestionPolicy.Worded.
        var result = AgentDishSuggestionPolicy.Worded("{name1}{restaurant1}{price1}" + words, Context(One), One);

        // STEP-03: Assert result is the expected value.
        result.Should().Be(shown ? "Bún ChảQuán A (1 Hàng Mành)45.000 đồng" + words : One.FixedReply);
    }

    // TEST-06: Two dishes keep their own slots; covers either order, and rejects slots split between dishes and a missing dish.
    /// <summary>The backend can fill the right fact only into the slot the model put next to the right dish.</summary>
    [Theory]
    [InlineData("{name1} ở {restaurant1}, {price1}.\n{name2} ở {restaurant2}, {price2}.", "Bún Chả ở Quán A, 45.000 đồng.\nPhở Gà ở Quán B, 50.000 đồng.")]
    [InlineData("{name2} ở {restaurant2}, {price2}.\n{name1} ở {restaurant1}, {price1}.", "Phở Gà ở Quán B, 50.000 đồng.\nBún Chả ở Quán A, 45.000 đồng.")]
    [InlineData("{name1} ở {restaurant2}, {price1}.\n{name2} ở {restaurant1}, {price2}.", null)]
    [InlineData("{name1} ở {restaurant1}, {price2}.\n{name2} ở {restaurant2}, {price1}.", null)]
    [InlineData("{name1} ở {restaurant1}, {price1}.", null)]
    public void Worded_TwoDishes_KeepTheirOwnSlots(string reply, string? shown) =>
        // STEP-01: Assert AgentDishSuggestionPolicy.Worded() is the expected value.
        AgentDishSuggestionPolicy.Worded(reply, Context(Two), Two).Should().Be(shown ?? Two.FixedReply);

    // TEST-07: An address slot needs an address; a sentence for no dish uses no slot; covers a dish without one, nothing found with and without a slot, and a number the user wrote.
    /// <summary>A slot with nothing to fill it would show a blank or fail.</summary>
    [Theory]
    [InlineData("two", "{name1} ở {restaurant1}, {price1}, {address1}.\n{name2} ở {restaurant2}, {price2}.", null)]
    [InlineData("none", "Trên ANGI chưa có món nào hợp, bạn bỏ bớt điều kiện nhé.", "Trên ANGI chưa có món nào hợp, bạn bỏ bớt điều kiện nhé.")]
    [InlineData("none", "Bạn thử {name1} nhé!", null)]
    [InlineData("one", "Dưới 60k có {name1} ở {restaurant1}, {price1} nhé!", "Dưới 60k có Bún Chả ở Quán A (1 Hàng Mành), 45.000 đồng nhé!")]
    public void Worded_SlotNeedsItsFact(string kind, string reply, string? shown)
    {
        // STEP-01: Prepare suggestion from kind, and context with a message stating a price.
        var suggestion = kind switch { "two" => Two, "one" => One, _ => new AgentDishSuggestion([], false, false, AgentDishSuggestionPolicy.NothingFound) };
        var context = Context(suggestion);
        context.Request.Content = "gợi ý món dưới 60k";

        // STEP-02: Assert AgentDishSuggestionPolicy.Worded() is the expected value.
        AgentDishSuggestionPolicy.Worded(reply, context, suggestion).Should().Be(shown ?? suggestion.FixedReply);
    }

    // TEST-08: A taste claim needs a real taste ranking; covers a claim with and without ranking, a plain admission, no claim at all, and a denial in another sentence that does not excuse the claim.
    /// <summary>Saying "theo khẩu vị của bạn" over a rating fallback would be false.</summary>
    [Theory]
    [InlineData("Theo khẩu vị của bạn, mình gợi ý {name1} ở {restaurant1}, {price1}.", true, true)]
    [InlineData("Theo khẩu vị của bạn, mình gợi ý {name1} ở {restaurant1}, {price1}.", false, false)]
    [InlineData("Món hợp gu bạn là {name1} ở {restaurant1}, {price1}.", false, false)]
    [InlineData("Mình chưa có đủ dữ liệu theo khẩu vị của bạn, nên tạm gợi ý {name1} ở {restaurant1}, {price1}.", false, true)]
    [InlineData("Mình gợi ý {name1} ở {restaurant1}, {price1}.", false, true)]
    [InlineData("Không có gì đâu. Theo khẩu vị của bạn thì {name1} ở {restaurant1}, {price1}.", false, false)]
    public void Worded_TasteClaim_NeedsRealRanking(string reply, bool personalized, bool shown)
    {
        // STEP-01: Create suggestion with the given ranking.
        var suggestion = One with { Personalized = personalized };

        // STEP-02: Assert AgentDishSuggestionPolicy.Worded() is the filled wording or the fixed reply.
        var result = AgentDishSuggestionPolicy.Worded(reply, Context(suggestion), suggestion);
        if (shown) result.Should().Contain("Bún Chả ở Quán A (1 Hàng Mành), 45.000 đồng").And.NotContain("{");
        else result.Should().Be(suggestion.FixedReply);
    }

    // TEST-09: The packed suggestion carries each dish's facts, the ranking, the pin and the same fixed reply as before; a dish with no address has none, and no dish gives the fixed line.
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

    // TEST-10: The invitation's example may name a day of the trip; covers a day inside it, the last day, a day past it and no trip.
    /// <summary>A day number is not a dish fact, but a day the trip does not have would mislead.</summary>
    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(3, 2, true)]
    [InlineData(2, 3, false)]
    [InlineData(null, 1, false)]
    public void Worded_InvitationExample_NamesADayOfTheTrip(int? numDays, int day, bool shown)
    {
        // STEP-01: Get context from Context and set context.State.NumDays to numDays.
        var context = Context(One);
        context.State = new AgentState { NumDays = (short?)numDays };

        // STEP-02: Prepare result through AgentDishSuggestionPolicy.Worded.
        var result = AgentDishSuggestionPolicy.Worded($"Thử {{name1}} ở {{restaurant1}}, {{price1}}. Muốn thêm thì nhắn mình, ví dụ bữa tối ngày {day} nha.", context, One);

        // STEP-03: Assert result holds the invitation, or is the fixed reply.
        if (shown) result.Should().EndWith($"ví dụ bữa tối ngày {day} nha.");
        else result.Should().Be(One.FixedReply);
    }

    // TEST-11: The fixed reply invites adding the dish only over a plan, in one form for one dish and for several, and the packed suggestion carries the same flag.
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

    // TEST-12: A refused sentence is reported with its reason; covers no slot, a slot for no dish, split slots, a missing slot, a typed number, a dish word, an unranked taste claim and a shown sentence.
    /// <summary>The reason is what tells a review why the model's wording keeps being replaced, without storing the wording.</summary>
    [Theory]
    [InlineData("Mình gợi ý Bún Chả nhé!", "one", "no_slot")]
    [InlineData("Thử {name2} ở {restaurant1}, {price1} nhé!", "one", "unknown_slot")]
    [InlineData("{name1} ở {restaurant2}, {price1}; {name2} ở {restaurant1}, {price2} nhé!", "two", "split_slot")]
    [InlineData("Thử {name1} ở {restaurant1} nhé!", "one", "missing_slot")]
    [InlineData("Thử {name1} ở {restaurant1}, {price1}, khoảng 99.000 đồng nhé!", "one", "number")]
    [InlineData("Thử {name1} ở {restaurant1}, {price1}, hoặc phở nhé!", "one", "dish_word")]
    [InlineData("Theo khẩu vị của bạn, thử {name1} ở {restaurant1}, {price1} nhé!", "one", "taste_claim")]
    [InlineData("Thử {name1} ở {restaurant1}, {price1} nhé!", "one", null)]
    // The example in the system prompt: a model copying it must not lose its wording to these checks.
    [InlineData("Mình hiểu là khá cay nhé. Bạn thử {name1} ở {restaurant1}, khoảng {price1}; muốn nhẹ hơn cứ nhắn mình.", "one", null)]
    public void WordingIssue_NamesTheReason(string reply, string dishes, string? reason)
    {
        // STEP-01: Prepare suggestion from dishes, and context with a dish word.
        var suggestion = dishes == "two" ? Two : One;
        var context = Context(suggestion);
        context.DishWords = new HashSet<string> { "phở" };

        // STEP-02: Assert AgentDishSuggestionPolicy.WordingIssue() is reason.
        AgentDishSuggestionPolicy.WordingIssue(reply, context, suggestion).Should().Be(reason);
    }

    // TEST-13: A sentence with no slot leads the fixed reply when its own words are safe; covers safe words, a typed dish name, a typed price and an unranked taste claim.
    /// <summary>The model's reading of the taste is the natural part of the reply, and the fixed reply already holds the facts.</summary>
    [Theory]
    [InlineData("Mình hiểu là khá cay nhé.", "Mình hiểu là khá cay nhé. Mình gợi ý Bún Chả.")]
    [InlineData("Bạn thử Bún Chả nhé.", "Mình gợi ý Bún Chả.")]
    [InlineData("Món này giá 45.000 đồng nhé.", "Mình gợi ý Bún Chả.")]
    [InlineData("Món này đúng khẩu vị của bạn nhé.", "Mình gợi ý Bún Chả.")]
    public void Worded_NoSlot_KeepsOnlySafeOpening(string reply, string expected)
    {
        // STEP-01: Assert AgentDishSuggestionPolicy.Worded() is expected.
        AgentDishSuggestionPolicy.Worded(reply, Context(One), One).Should().Be(expected);
    }

    // TEST-14: What the user needs to act on is added when the model leaves it out; covers an address, meals, both, and a sentence that placed them itself.
    /// <summary>Conversation 158 repeated "nhưng nhà tôi Cầu Giấy" because no reply said where the restaurant was, and suggested a breakfast dish without saying so.</summary>
    [Theory]
    [InlineData("1 Hàng Mành", null, "Thử {name1} ở {restaurant1}, {price1} nhé!", "Thử Bún Chả ở Quán A (1 Hàng Mành), 45.000 đồng nhé!")]
    [InlineData(null, "bữa sáng", "Thử {name1} ở {restaurant1}, {price1} nhé!", "Thử Bún Chả (hợp bữa sáng) ở Quán A, 45.000 đồng nhé!")]
    [InlineData("1 Hàng Mành", "bữa sáng", "Thử {name1} ở {restaurant1}, {price1} nhé!", "Thử Bún Chả (hợp bữa sáng) ở Quán A (1 Hàng Mành), 45.000 đồng nhé!")]
    [InlineData("1 Hàng Mành", "bữa sáng", "Sáng mai thử {name1} cho {meals1} ở {restaurant1}, {address1}, {price1} nhé!", "Sáng mai thử Bún Chả cho bữa sáng ở Quán A, 1 Hàng Mành, 45.000 đồng nhé!")]
    public void Worded_LeftOutFacts_AreAdded(string? address, string? meals, string reply, string shown)
    {
        // STEP-01: Create suggestion with the given facts.
        var suggestion = new AgentDishSuggestion([new("Bún Chả", "Quán A", 45000, address, meals)], false, false, "Mình gợi ý Bún Chả.");

        // STEP-02: Assert AgentDishSuggestionPolicy.Worded() is shown.
        AgentDishSuggestionPolicy.Worded(reply, Context(suggestion), suggestion).Should().Be(shown);
    }

    // TEST-15: A meals slot needs the dish to have meals, like an address slot needs an address.
    /// <summary>A slot with nothing to fill it would throw or show a blank.</summary>
    [Fact]
    public void WordingIssue_MealsSlotWithoutMeals_IsUnknown()
    {
        // STEP-01: Assert the wording issue for a dish with no meals.
        AgentDishSuggestionPolicy.WordingIssue("Thử {name1} {meals1} ở {restaurant1}, {price1} nhé!", Context(One), One).Should().Be("unknown_slot");
    }

    // TEST-16: The fixed reply names the place searched and the place not found, states meals unless a meal was asked for, and gives every dish its address; the packed facts match.
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

    // TEST-17: Words around the slots may name the place searched or not found, even when this message did not.
    /// <summary>"Cầu Giấy" comes from an earlier message; the grounding check must not throw the sentence away for naming it.</summary>
    [Theory]
    [InlineData("Quanh Cầu Giấy có {name1} ở {restaurant1}, {price1} nhé!", "Cầu Giấy", null, true)]
    [InlineData("Chưa thấy Xóm Núi, thử {name1} ở {restaurant1}, {price1} nhé!", null, "Xóm Núi", true)]
    [InlineData("Quanh Tây Hồ có {name1} ở {restaurant1}, {price1} nhé!", "Cầu Giấy", null, false)]
    public void Worded_AreaWords_AreBackedByTheSuggestion(string reply, string? area, string? notFound, bool shown)
    {
        // STEP-01: Create suggestion with the given place.
        var suggestion = One with { Area = area, AreaNotFound = notFound };

        // STEP-02: Assert the wording is kept only when the suggestion backs the place.
        AgentDishSuggestionPolicy.WordingIssue(reply, Context(suggestion), suggestion).Should().Be(shown ? null : "name");
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
