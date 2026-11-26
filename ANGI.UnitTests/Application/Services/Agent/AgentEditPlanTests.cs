using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>
/// edit_plan: the model reads an edit into ops, the backend finds each target and new dish and builds the changes itself, under the state the draft keeps
/// (conversations 13 to 20: drafts guessed as add or replace, a drink put on breakfast, "đồ uống" searched as a dish name, rice and a drink in one request).
/// </summary>
public sealed class AgentEditPlanTests
{
    private static readonly Tag Drink = new() { Id = 40, Name = "Đồ uống", Code = "drink", TagType = TagType.Category };
    private static readonly Tag Main = new() { Id = 41, Name = "Món chính", Code = AgentScopedPlanning.MainTagCode, TagType = TagType.Category };

    // TEST-01: A kind on a day with no meal named is asked, never put on a meal the backend picks (conversation 17 put the drink on breakfast).
    /// <summary>Nothing is prepared and nothing is granted; the result lists the day's meals to ask about.</summary>
    [Fact]
    public async Task Add_KindWithoutMeal_AsksWhichMeal()
    {
        var (tools, context) = Setup();

        var result = await tools.ExecuteAsync(context, Call(new AgentEditOp("add", DayNumber: 1, KindTagIds: [40])), CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("needs_input");
        var first = result.GetProperty("results")[0];
        first.GetProperty("status").GetString().Should().Be("needs_meal");
        first.GetProperty("meals").EnumerateArray().Select(meal => meal.GetString()).Should().Equal("breakfast", "lunch", "dinner");
        context.PreparedChanges.Should().BeNull();
        context.Grants.Should().BeEmpty();
    }

    // TEST-02: A kind on a named meal is the best dish of that kind there, added next to the meal's dish with a grant under the unchanged state.
    /// <summary>The state's meal requests stay as they were, so a later replan never turns the meal into a drink only (conversation 17).</summary>
    [Fact]
    public async Task Add_KindOnMeal_PlacesBesideTheMealWithoutTouchingState()
    {
        var (tools, context) = Setup();
        var before = context.State;

        var result = await tools.ExecuteAsync(context, Call(new AgentEditOp("add", DayNumber: 1, MealSlot: "lunch", KindTagIds: [40])), CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("ready");
        var change = context.PreparedChanges.Should().ContainSingle().Subject;
        change.Type.Should().Be("add");
        change.Item.Should().Match<AgentItem>(item => item.DayNumber == 1 && item.MealSlot == "lunch" && item.DishId == 5 && item.ItemKey == "" && item.SortOrder == 1);
        context.State.MealRequests.Should().BeEquivalentTo(before.MealRequests);
        var grant = context.Grants.Should().ContainSingle().Subject;
        grant.DishIds.Should().Equal(5);
        AgentScopedPlanning.Fingerprint(grant.Constraints, 1, "lunch").Should().Be(AgentScopedPlanning.Fingerprint(context.State, 1, "lunch"));
    }

    // TEST-03: "đồ uống" sent as a dish name is pointed back to its catalog tag instead of being searched as a dish (conversation 20).
    /// <summary>The comparison is with catalog tag names, never with the user's wording.</summary>
    [Fact]
    public async Task Add_KindNameAsDishName_IsSentBackToItsTag()
    {
        var (tools, context) = Setup();

        var result = await tools.ExecuteAsync(context, Call(new AgentEditOp("add", DayNumber: 1, MealSlot: "lunch", DishName: "đồ uống")), CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        result.GetProperty("warnings")[0].GetString().Should().Contain("kindTagIds=[40]");
        context.PreparedChanges.Should().BeNull();
    }

    // TEST-04: An op's shape is checked, and only a plan can be edited.
    /// <summary>A double target, a dish on a removal and a chat with no plan are sent back to the model.</summary>
    [Fact]
    public async Task BadOps_AreSentBack()
    {
        var (tools, context) = Setup();
        (await tools.ExecuteAsync(context, Call(new AgentEditOp("add", DayNumber: 1, MealSlot: "lunch", DishId: 6, KindTagIds: [40])), CancellationToken.None))
            .GetProperty("status").GetString().Should().Be("invalid_arguments");
        (await tools.ExecuteAsync(context, Call(new AgentEditOp("remove", DayNumber: 1, MealSlot: "lunch", DishId: 6)), CancellationToken.None))
            .GetProperty("status").GetString().Should().Be("invalid_arguments");

        var (noPlanTools, noPlan) = Setup(withPlan: false);
        (await noPlanTools.ExecuteAsync(noPlan, Call(new AgentEditOp("add", DayNumber: 1, MealSlot: "lunch", DishId: 6)), CancellationToken.None))
            .GetProperty("status").GetString().Should().Be("invalid_arguments");
    }

    // TEST-05: A dish picked by id is that exact dish (an id alone was ignored and another dish ranked in, replay of conversation 16); with no meal it goes
    // to the meal it fits on that day, main meals first; a longer trip with no day is asked.
    /// <summary>Dish 6 fits lunch and dinner and is put on lunch; with no day the result asks for one.</summary>
    [Fact]
    public async Task Add_DishWithoutMeal_IsThatDishOnTheMealItFits_AndNoDayIsAsked()
    {
        var (tools, context) = Setup();

        var placed = await tools.ExecuteAsync(context, Call(new AgentEditOp("add", DayNumber: 1, DishId: 6)), CancellationToken.None);
        placed.GetProperty("status").GetString().Should().Be("ready");
        context.PreparedChanges.Should().ContainSingle().Which.Item.Should().Match<AgentItem>(item => item.MealSlot == "lunch" && item.DishId == 6);

        var noDay = await tools.ExecuteAsync(context, Call(new AgentEditOp("add", DishId: 6)), CancellationToken.None);
        noDay.GetProperty("results")[0].GetProperty("status").GetString().Should().Be("needs_day");
        context.PreparedChanges.Should().BeNull();
    }

    // TEST-06: Replacing the meal's only main dish by a named dish keeps its key and stores the request, so a replan still serves it.
    /// <summary>The replacement change targets lunch's item; the state gains the lunch request and the grant is issued under that state.</summary>
    [Fact]
    public async Task Replace_MainDishByName_KeepsKeyAndStoresTheRequest()
    {
        var (tools, context) = Setup();

        var result = await tools.ExecuteAsync(context, Call(new AgentEditOp("replace", DayNumber: 1, MealSlot: "lunch", DishId: 6)), CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("ready");
        var change = context.PreparedChanges.Should().ContainSingle().Subject;
        change.Should().Match<AgentChange>(c => c.Type == "replace" && c.TargetItemKey == "l" && c.Item!.ItemKey == "l" && c.Item.DishId == 6);
        context.State.MealRequests.Should().ContainSingle(request => request.DayNumber == 1 && request.MealSlot == "lunch" && request.DishId == 6);
        var grant = context.Grants.Should().ContainSingle().Subject;
        AgentScopedPlanning.Fingerprint(grant.Constraints, 1, "lunch").Should().Be(AgentScopedPlanning.Fingerprint(context.State, 1, "lunch"));
    }

    // TEST-07: A replacement in a meal with several dishes is asked about (which dish), and a removal takes the key, the meal, the day or the meal on every day.
    /// <summary>needs_target lists the meal's dishes; removals list exactly the items that go.</summary>
    [Fact]
    public async Task Replace_InMealWithTwoDishes_AsksWhich_AndRemovalsTakeWhatTheyName()
    {
        var (tools, context) = Setup(extra: AgentTestData.Item(5, "l2", 1, "lunch") with { SortOrder = 1 });

        var which = await tools.ExecuteAsync(context, Call(new AgentEditOp("replace", DayNumber: 1, MealSlot: "lunch")), CancellationToken.None);
        which.GetProperty("results")[0].GetProperty("status").GetString().Should().Be("needs_target");
        which.GetProperty("results")[0].GetProperty("dishes").GetArrayLength().Should().Be(2);
        context.PreparedChanges.Should().BeNull();

        await tools.ExecuteAsync(context, Call(new AgentEditOp("remove", TargetItemKey: "l2")), CancellationToken.None);
        context.PreparedChanges!.Select(change => change.TargetItemKey).Should().Equal("l2");
        await tools.ExecuteAsync(context, Call(new AgentEditOp("remove", DayNumber: 1, MealSlot: "lunch")), CancellationToken.None);
        context.PreparedChanges!.Select(change => change.TargetItemKey).Should().BeEquivalentTo(new[] { "l", "l2" });
        await tools.ExecuteAsync(context, Call(new AgentEditOp("remove", DayNumber: 1)), CancellationToken.None);
        context.PreparedChanges!.Should().HaveCount(4).And.OnlyContain(change => change.Type == "remove");
        await tools.ExecuteAsync(context, Call(new AgentEditOp("remove", MealSlot: "dinner")), CancellationToken.None);
        context.PreparedChanges!.Select(change => change.TargetItemKey).Should().Equal("d");
    }

    /// <summary>A two-day trip with breakfast 1, lunch 2 (a main dish) and dinner 3 on day 1; dish 5 is a drink, dish 6 a main dish serving lunch and dinner.</summary>
    private static (AgentToolExecutor Tools, AgentContext Context) Setup(bool withPlan = true, AgentItem? extra = null)
    {
        var drink = AgentTestData.Dish(5); drink.Name = "Trà đá"; drink.DishTags.Add(new() { TagId = Drink.Id, Tag = Drink });
        var main = AgentTestData.Dish(6); main.Name = "Cơm thố"; main.DishTags.Add(new() { TagId = Main.Id, Tag = Main });
        var lunch = AgentTestData.Dish(2); lunch.DishTags.Add(new() { TagId = Main.Id, Tag = Main });
        var fixture = AgentTestData.Validator(AgentTestData.Dish(1), lunch, AgentTestData.Dish(3), drink, main);
        fixture.Tags.Setup(x => x.GetTagsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Drink, Main });
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) =>
                filter.DishIds.Count > 0 ? filter.CompatibleMealTimeCode is null or "lunch" or "dinner" ? filter.DishIds.ToArray() : []
                : filter.TagIds.Contains(Drink.Id) ? new[] { 5 } : new[] { 1, 6 });
        AgentItem[] items = [AgentTestData.Item(1, "b", 1, "breakfast"), AgentTestData.Item(2, "l", 1, "lunch"), AgentTestData.Item(3, "d", 1, "dinner"), .. extra is null ? Array.Empty<AgentItem>() : [extra]];
        var context = AgentTestData.Context(withPlan ? AgentTestData.Snapshot(items) : null);
        context.State = context.State with { MealSlots = ["breakfast", "lunch", "dinner"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>().Object);
        return (tools, context);
    }

    /// <summary>Serializes the strict edit_plan call: every op field present, kindTagIds an empty array when unused.</summary>
    private static AgentToolCall Call(params AgentEditOp[] ops) => new("e", "edit_plan", JsonSerializer.SerializeToElement(new
    {
        ops = ops.Select(op => new { op = op.Op, targetItemKey = op.TargetItemKey, dayNumber = op.DayNumber, mealSlot = op.MealSlot, dishId = op.DishId, dishName = op.DishName, kindTagIds = op.KindTagIds ?? [] })
    }));
}
