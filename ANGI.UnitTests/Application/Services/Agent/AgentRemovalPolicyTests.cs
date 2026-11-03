using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies only unambiguous whole-message removals bypass the model, and that emptied meals are recorded as skipped.</summary>
public sealed class AgentRemovalPolicyTests
{
    /// <summary>Builds a two-day draft with named dishes for breakfast, lunch and dinner of day one and lunch of day two.</summary>
    private static AgentContext Context(string content)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(
            AgentTestData.Item(1, "a", 1, "breakfast") with { DishName = "Phở Gà" },
            AgentTestData.Item(2, "b", 1, "lunch") with { DishName = "Bún Chả" },
            AgentTestData.Item(3, "c", 1, "dinner") with { DishName = "Cơm Tấm" },
            AgentTestData.Item(4, "d", 2, "lunch") with { DishName = "Mì Quảng" }));
        context.State = context.State with { MealSlots = ["breakfast", "lunch", "dinner"] };
        context.Request.Content = content;
        return context;
    }

    // TEST-01: A meal with its day or an exact dish name resolves the items; covers meal words, numbered meals, day-first wording, polite wording,
    // an unaccented message, two meals, a dish name with and without "món", a whole day and a meal on every day.
    /// <summary>Ensures removals the backend can read never spend an LLM call.</summary>
    [Theory]
    [InlineData("Xóa bữa tối ngày 1", "c")]
    [InlineData("bỏ bữa 3 ngày 1 đi", "c")]
    [InlineData("xóa ngày 2 bữa trưa nhé", "d")]
    [InlineData("hãy xóa bữa trưa ngày 2 giúp tôi.", "d")]
    [InlineData("xoa bua sang ngay 1", "a")]
    [InlineData("xóa bữa sáng ngày 1 và bữa trưa ngày 2", "a,d")]
    [InlineData("bỏ món Phở Gà", "a")]
    [InlineData("Xóa Bún Chả đi", "b")]
    [InlineData("xóa ngày 2", "d")]
    [InlineData("bỏ các món ngày 1", "a,b,c")]
    [InlineData("bỏ bữa trưa tất cả các ngày", "b,d")]
    [InlineData("xóa bữa trưa toàn chuyến", "b,d")]
    public void Targets_UnambiguousRemoval_ReturnsItems(string content, string keys)
    {
        AgentRemovalPolicy.Targets(Context(content))!.Select(item => item.ItemKey).Should().Equal(keys.Split(','));
    }

    // TEST-02: Anything else reaches the model; covers an edit, "bổ sung", a removal with a replacement, an unknown dish, a meal without a day and no matching item.
    /// <summary>Keeps the rule narrow so a real edit is never turned into a removal.</summary>
    [Theory]
    [InlineData("đổi bữa tối ngày 1")]
    [InlineData("bổ sung bữa tối ngày 1")]
    [InlineData("xóa bữa tối ngày 1 thay bằng phở")]
    [InlineData("bỏ món Phở Bò")]
    [InlineData("bỏ bữa sáng")]
    [InlineData("xóa bữa tối ngày 2")]
    public void Targets_NotUnambiguous_ReturnsNull(string content)
    {
        AgentRemovalPolicy.Targets(Context(content)).Should().BeNull();
    }

    // TEST-03: No draft, a pending constraint change, a pending planning request or a replan reaches the model.
    /// <summary>Ensures a removal cannot skip an open confirmation or a turn without a plan.</summary>
    [Theory]
    [InlineData("no-source")][InlineData("pending-change")][InlineData("pending-plan")][InlineData("replan")]
    public void Targets_NoPlanOrOpenFlow_ReturnsNull(string area)
    {
        var context = area == "no-source" ? AgentTestData.Context() : Context("xóa bữa tối ngày 1");
        context.Request.Content = "xóa bữa tối ngày 1";
        if (area == "pending-change") context.State = context.State with { PendingStateChange = AgentTestData.State() };
        if (area == "pending-plan") context.State = context.State with { PendingPlanningRequest = new(["x"]) };
        if (area == "replan") context.ReplanAfterConfirmation = true;

        AgentRemovalPolicy.Targets(context).Should().BeNull();
    }

    // TEST-04: A meal holding a dish or restaurant the user named goes to the model so the constraint-change confirmation still applies.
    /// <summary>Prevents a removal from silently dropping a named request.</summary>
    [Fact]
    public void Targets_NamedRequestAtMeal_ReturnsNull()
    {
        var context = Context("xóa bữa trưa ngày 1");
        context.State = context.State with { MealRequests = [new(1, "lunch", "Bún Chả", null, [])] };

        AgentRemovalPolicy.Targets(context).Should().BeNull();
    }

    // TEST-05: A bare removal resolves the item selected in the UI; other wording with a selection goes to the model.
    /// <summary>Lets the frontend remove the selected item without a provider call.</summary>
    [Theory]
    [InlineData("xóa món này", true)]
    [InlineData("xóa bữa tối ngày 1", false)]
    public void Targets_SelectedItem_UsesSelection(string content, bool resolved)
    {
        var context = Context(content);
        context.Request.SelectedItemKey = "b";

        var items = AgentRemovalPolicy.Targets(context);

        if (resolved) items!.Single().ItemKey.Should().Be("b");
        else items.Should().BeNull();
    }

    // TEST-06: Removal wording is recognized for the model path; covers removal verbs, "bổ sung" and an ordinary edit.
    /// <summary>Ensures a model patch empties a meal only when the user asked to remove something.</summary>
    [Theory]
    [InlineData("xóa món tối và đổi món sáng", true)]
    [InlineData("bỏ bớt món đi", true)]
    [InlineData("bổ sung món", false)]
    [InlineData("đổi món", false)]
    public void StatesRemoval_ReadsVerb(string content, bool expected)
    {
        AgentRemovalPolicy.StatesRemoval(content).Should().Be(expected);
    }

    // TEST-07: Emptied meals are recorded, a meal with a dish again is dropped, and an empty result stays null so state has no empty field.
    /// <summary>Keeps skipped meals exact so a re-added meal is required again after later removals.</summary>
    [Fact]
    public void WithSkippedMeals_TracksOnlyEmptyMeals()
    {
        var source = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1, "lunch"), AgentTestData.Item(2, "b", 1, "dinner"));
        var state = AgentTestData.State() with { SkippedMeals = [new(2, "lunch"), new(1, "dinner")] };

        AgentRemovalPolicy.WithSkippedMeals(state, source, [AgentTestData.Item(2, "b", 1, "dinner")]).SkippedMeals
            .Should().BeEquivalentTo(new[] { new AgentScope(2, "lunch"), new AgentScope(1, "lunch") });
        AgentRemovalPolicy.Uncovered([new(1, "lunch")], [AgentTestData.Item(1, "a", 1, "lunch")]).Should().BeNull();
    }

    // TEST-08: A meal without a day asks which days are meant; covers a meal in the plan, a meal the plan lacks, explicit every-day wording,
    // an edit and no plan.
    /// <summary>Prevents "bỏ bữa sáng" being read by the model as replacing one breakfast.</summary>
    [Theory]
    [InlineData("bỏ bữa sáng", true)]
    [InlineData("xóa bữa trưa đi", true)]
    [InlineData("bỏ bữa khuya", false)]
    [InlineData("bỏ bữa sáng toàn chuyến", false)]
    [InlineData("đổi bữa sáng", false)]
    public void Question_MealWithoutDay_AsksDays(string content, bool asks)
    {
        var question = AgentRemovalPolicy.Question(Context(content));

        if (asks) question.Should().Contain("tất cả các ngày");
        else question.Should().BeNull();
        var planless = AgentTestData.Context(); planless.Request.Content = content;
        AgentRemovalPolicy.Question(planless).Should().BeNull();
    }
}
