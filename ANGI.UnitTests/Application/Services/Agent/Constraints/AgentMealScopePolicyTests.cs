using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies explicit day/meal wording is read deterministically and keeps a meal taste on that meal.</summary>
public sealed class AgentMealScopePolicyTests
{
    // TEST-01: A meal and a day are paired in either order with default breakfast/lunch/dinner numbering; covers bữa 2 ngày 1,
    // bữa trưa của ngày 1, ngày 1 bữa 2, "ngày 1, bữa tối", bữa thứ hai ngày 3, bữa đầu tiên ngày 2 and bữa cuối ngày 4.
    /// <summary>Checks every supported wording resolves to the same day/meal scope a person would mean.</summary>
    [Theory]
    [InlineData("Vừa, áp dụng cho bữa 2 ngày 1", 1, "lunch")]
    [InlineData("Áp dụng cho bữa trưa của ngày 1", 1, "lunch")]
    [InlineData("Ngày 1 bữa 2 nhé", 1, "lunch")]
    [InlineData("Ngày 1, bữa tối", 1, "dinner")]
    [InlineData("Bữa thứ hai ngày 3", 3, "lunch")]
    [InlineData("Bữa đầu tiên ngày 2", 2, "breakfast")]
    [InlineData("Bữa cuối ngày 4", 4, "dinner")]
    public void Read_MealWithDay_ResolvesScope(string content, short day, string meal)
    {
        var references = AgentMealScopePolicy.Read(content, new AgentState());

        references.Meals.Should().Equal(new AgentScope(day, meal));
        references.Days.Should().BeEmpty();
        references.OutOfRange.Should().BeFalse();
    }

    // TEST-02: "Bữa N" counts the declared meals in breakfast-to-late-night order; covers two declared meals and a declared snack as the last meal.
    /// <summary>Ensures numbering follows the plan's own meals instead of a fixed breakfast/lunch/dinner list.</summary>
    [Theory]
    [InlineData(new[] { "dinner", "breakfast" }, "bữa 2 ngày 1", "dinner")]
    [InlineData(new[] { "snack", "lunch", "dinner" }, "bữa cuối ngày 1", "snack")]
    public void Read_DeclaredMeals_DefineNumbering(string[] mealSlots, string content, string meal)
    {
        AgentMealScopePolicy.Read(content, new AgentState { MealSlots = mealSlots }).Meals.Should().Equal(new AgentScope(1, meal));
    }

    // TEST-03: A day named without a meal is reported separately from a meal paired with a day, and a duration is not a day reference.
    /// <summary>Separates "cả ngày 3" from "bữa 2 ngày 1" so a whole-day taste is not narrowed by mistake.</summary>
    [Fact]
    public void Read_StandaloneDay_IsReportedSeparately()
    {
        var references = AgentMealScopePolicy.Read("Hà Nội 4 ngày, chua cho cả ngày 3 và bữa 2 ngày 1", new AgentState());

        references.Meals.Should().Equal(new AgentScope(1, "lunch"));
        references.Days.Should().Equal((short)3);
    }

    // TEST-04: A meal number beyond the meals per day is out of range and produces a question listing the meals; covers bữa 4 with the default
    // three meals, bữa 4 with four declared meals and bữa 0.
    /// <summary>Makes the backend ask instead of guessing when a numbered meal does not exist in the plan.</summary>
    [Theory]
    [InlineData("Đổi bữa 4 ngày 1", null, true)]
    [InlineData("Đổi bữa 4 ngày 1", new[] { "breakfast", "lunch", "dinner", "snack" }, false)]
    [InlineData("Đổi bữa 0 ngày 1", null, true)]
    public void Question_MealNumberBeyondPlan_AsksWhichMeal(string content, string[]? mealSlots, bool asks)
    {
        var state = new AgentState { MealSlots = mealSlots ?? [] };

        var question = AgentMealScopePolicy.Question(content, state);

        AgentMealScopePolicy.Read(content, state).OutOfRange.Should().Be(asks);
        if (asks) question.Should().Be("Mỗi ngày trong lịch có 3 bữa (bữa sáng, bữa trưa, bữa tối). Bạn muốn chọn bữa nào?");
        else question.Should().BeNull();
    }

    // TEST-05: A whole-day override newly proposed for a day whose meals were named becomes one override per named meal with the same targets.
    /// <summary>Checks the narrowing keeps the targets and splits them over every meal the user named for that day.</summary>
    [Fact]
    public void Narrow_NewWholeDayOverride_BecomesNamedMealOverrides()
    {
        var targets = new AgentTasteTarget[] { new("sour", .5) };
        var proposed = new AgentState { NumDays = 2, TasteOverrides = [new(1, null, targets), new(2, null, targets)] };

        var narrowed = AgentMealScopePolicy.Narrow(proposed, new AgentState(), ["Chua vừa cho bữa sáng ngày 1 và bữa tối ngày 1, cả ngày 2"])!;

        narrowed.TasteOverrides.Select(x => (x.DayNumber, x.MealSlot)).Should()
            .Equal(((short)1, "breakfast"), ((short)1, "dinner"), ((short)2, (string?)null));
        narrowed.TasteOverrides.Should().OnlyContain(x => x.Targets.SequenceEqual(targets));
    }

    // TEST-06: Narrowing leaves the state untouched when it cannot be sure; covers the same day also named without a meal, an override already
    // stored in the current state, an existing override for the named meal, no meal wording and a null proposal.
    /// <summary>Ensures narrowing never rewrites a whole-day taste the user actually asked for or an older taste.</summary>
    [Theory]
    [InlineData("day-also-named")][InlineData("already-stored")][InlineData("meal-override-exists")][InlineData("no-meal-wording")][InlineData("null")]
    public void Narrow_Uncertain_KeepsProposal(string area)
    {
        var day = new AgentTasteOverride(1, null, [new("sour", .5)]);
        var proposed = new AgentState { NumDays = 2, TasteOverrides = area == "meal-override-exists"
            ? [day, new(1, "lunch", [new("sweet", .75)])] : [day] };
        var current = area == "already-stored" ? new AgentState { NumDays = 2, TasteOverrides = [new(1, null, [new("sour", .5)])] } : new AgentState();
        var message = area switch
        {
            "day-also-named" => "Chua vừa cả ngày 1, nhất là bữa 2 ngày 1",
            "no-meal-wording" => "Chua vừa cho ngày 1",
            _ => "Chua vừa cho bữa 2 ngày 1"
        };

        var narrowed = AgentMealScopePolicy.Narrow(area == "null" ? null : proposed, current, [message]);

        if (area == "null") narrowed.Should().BeNull();
        else narrowed.Should().BeSameAs(proposed);
    }

    // TEST-07: Evidence is the retained clarification messages when present, otherwise the current message only when it declares a taste;
    // covers pending messages, a taste declaration and an edit request.
    /// <summary>Limits narrowing to wording that is about taste, so "đổi bữa 1 ngày 1" cannot re-scope a stored taste.</summary>
    [Theory]
    [InlineData(true, "Vừa, bữa 2 ngày 1", 2)]
    [InlineData(false, "Tôi thích ăn chua vừa cho bữa 2 ngày 1", 1)]
    [InlineData(false, "Đổi món khác cho bữa 1 ngày 1", 0)]
    public void Evidence_UsesPendingMessagesOrTasteDeclaration(bool pending, string content, int count)
    {
        var context = AgentTestData.Context();
        context.Request.Content = content;
        if (pending) context.State = context.State with { PendingPlanningRequest = new(["Tôi thích ăn chua", content]) };

        AgentMealScopePolicy.Evidence(context).Should().HaveCount(count);
    }
}
