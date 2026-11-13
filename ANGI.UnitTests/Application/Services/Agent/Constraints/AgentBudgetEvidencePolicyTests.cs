using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies a new budget needs the user's own words and a figure of the same size; no test here depends on how the user phrases it.</summary>
public sealed class AgentBudgetEvidencePolicyTests
{
    private static AgentContext Context(string content, AgentState? state = null, bool replan = false)
    {
        var context = AgentTestData.Context();
        context.State = state ?? new AgentState();
        context.Request.Content = content;
        context.ReplanAfterConfirmation = replan;
        return context;
    }

    // TEST-01: A budget backed by a quote passes; covers a daily amount multiplied by the days, a total, group separators, a decimal unit, accents, and slang no pattern can read.
    /// <summary>The model reads any wording; only a quote that was really written and a figure of the right size are required.</summary>
    [Theory]
    [InlineData("500k 1 ngày", "500k 1 ngày", 3, 1500000)]
    [InlineData("khoảng 2 triệu", "khoảng 2 triệu", 3, 2000000)]
    [InlineData("ngân sách 1.500.000 đồng", "1.500.000 đồng", 3, 1500000)]
    [InlineData("tầm 1,5tr thôi", "1,5tr", 3, 1500000)]
    [InlineData("KHOANG 2 TRIEU", "khoảng 2 triệu", 3, 2000000)]
    [InlineData("tầm 2 củ", "tầm 2 củ", 3, 2000000)]
    [InlineData("năm trăm một ngày", "năm trăm một ngày", 3, 1500000)]
    [InlineData("1 triệu rưỡi", "1 triệu rưỡi", 3, 1500000)]
    public void Unbacked_QuoteWrittenAndSizeFits_IsFalse(string message, string quote, short days, int budget)
    {
        // STEP-01: Create proposed as AgentState.
        var proposed = new AgentState { NumDays = days, BudgetAmount = budget, BudgetQuote = quote };

        // STEP-02: Assert AgentConstraintGuard.HasUnbackedBudget() is false.
        AgentConstraintGuard.HasUnbackedBudget(Context(message), proposed).Should().BeFalse();
    }

    // TEST-02: A budget nothing backs is refused; covers no quote, a quote the user never wrote, and the lost unit that turned "500k 1 ngày" into 1,500 đồng.
    /// <summary>Stops an invented or misread figure before it can reach a plan.</summary>
    [Theory]
    [InlineData("đi Hà Nội 3 ngày", null, 1500000)]
    [InlineData("đi Hà Nội 3 ngày", "ngân sách 2 triệu", 2000000)]
    [InlineData("500k 1 ngày", "500k 1 ngày", 1500)]
    [InlineData("khoảng 2 triệu", "khoảng 2 triệu", 2000)]
    public void Unbacked_NoEvidenceOrWrongSize_IsTrue(string message, string? quote, int budget)
    {
        // STEP-01: Create proposed as AgentState.
        var proposed = new AgentState { NumDays = 3, BudgetAmount = budget, BudgetQuote = quote };

        // STEP-02: Assert AgentConstraintGuard.HasUnbackedBudget() is true.
        AgentConstraintGuard.HasUnbackedBudget(Context(message), proposed).Should().BeTrue();
    }

    // TEST-03: Nothing is asked of a budget that did not change, of no budget and of a change already confirmed; an earlier message may back the quote.
    /// <summary>Keeps the check on new figures, so copying the state back or replanning after a yes costs no repair.</summary>
    [Fact]
    public void Unbacked_NoNewBudget_IsFalse()
    {
        // STEP-01: Prepare the state with a budget and a context whose history states another.
        var state = new AgentState { BudgetAmount = 1000000 };
        var earlier = Context("ok");
        earlier.History.Add(new("user", "tầm 2 triệu nhé"));

        // STEP-02: Assert each case is false.
        AgentConstraintGuard.HasUnbackedBudget(Context("đổi món", state), state).Should().BeFalse();
        AgentConstraintGuard.HasUnbackedBudget(Context("đổi món", state), new AgentState()).Should().BeFalse();
        AgentConstraintGuard.HasUnbackedBudget(Context("ok", state, replan: true), state with { BudgetAmount = 2000000 }).Should().BeFalse();
        AgentConstraintGuard.HasUnbackedBudget(earlier, new AgentState { BudgetAmount = 2000000, BudgetQuote = "tầm 2 triệu" }).Should().BeFalse();
    }

    // TEST-04: Exactly the backend's offered budget needs no quote; any other unquoted amount, such as the offer multiplied by the days, is still refused.
    /// <summary>Fixes "có" to "Mình nâng ngân sách lên 370.000 đồng nhé?" being refused because the user never typed the figure (S1).</summary>
    [Fact]
    public void Unbacked_BackendOffer_IsFalseOnlyForThatAmount()
    {
        // STEP-01: Prepare a state holding the offer.
        var state = new AgentState { NumDays = 3, BudgetAmount = 300000, BudgetOffer = 370000 };

        // STEP-02: Assert the offer passes and a misread multiple does not.
        AgentConstraintGuard.HasUnbackedBudget(Context("có", state), state with { BudgetAmount = 370000 }).Should().BeFalse();
        AgentConstraintGuard.HasUnbackedBudget(Context("có", state), state with { BudgetAmount = 1110000, BudgetQuote = "370000" }).Should().BeTrue();
    }

    // TEST-05: A per-day quote copied as the trip budget is multiplied by the days; covers "/ngày", "mỗi ngày" and "1 ngày", an amount already multiplied,
    // a whole-trip quote, a one-day trip, an unchanged budget and a length known only from the current state.
    /// <summary>Conversation 573 stored "500k/ngày" as 500.000 for a 3-day trip; 570 got 1.500.000 from the same words.</summary>
    [Theory]
    [InlineData("500k/ngày", 500000, (short)3, 1500000)]
    [InlineData("500k mỗi ngày", 500000, (short)3, 1500000)]
    [InlineData("500k 1 ngày", 500000, (short)2, 1000000)]
    [InlineData("500k/ngày", 1500000, (short)3, 1500000)]
    [InlineData("500k cả chuyến", 500000, (short)3, 500000)]
    [InlineData("500k/ngày", 500000, (short)1, 500000)]
    public void WithDailyBudget_MultipliesOnlyACopiedDailyFigure(string quote, int amount, short days, int expected)
    {
        // STEP-01: Create the proposed state.
        var proposed = new AgentState { NumDays = days, BudgetAmount = amount, BudgetQuote = quote };

        // STEP-02: Assert the trip budget.
        AgentConstraintGuard.WithDailyBudget(proposed, new AgentState())!.BudgetAmount.Should().Be(expected);
    }

    // TEST-06: An unchanged budget is left alone, and a trip length missing from the proposal is taken from the current state.
    /// <summary>The model often leaves out values it did not change; the check must still know the trip's days.</summary>
    [Fact]
    public void WithDailyBudget_UsesCurrentStateAndKeepsUnchanged()
    {
        // STEP-01: Create a current 3-day state with a budget, and proposals without a length.
        var current = new AgentState { NumDays = 3, BudgetAmount = 500000 };

        // STEP-02: Assert the unchanged budget stays and a new daily one is multiplied by the current length.
        AgentConstraintGuard.WithDailyBudget(new AgentState { BudgetAmount = 500000, BudgetQuote = "500k/ngày" }, current)!.BudgetAmount.Should().Be(500000);
        AgentConstraintGuard.WithDailyBudget(new AgentState { BudgetAmount = 400000, BudgetQuote = "400k/ngày" }, current)!.BudgetAmount.Should().Be(1200000);
    }
}
