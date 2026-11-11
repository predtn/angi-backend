using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies a taste level reaches the plan only with the user's own words for it, without the backend reading the language itself.</summary>
public sealed class AgentTasteEvidencePolicyTests
{
    // TEST-01: A new target is backed only by words the user wrote that say more than the taste; covers a level, another spelling, no accents, spoken wording, no quote, an empty quote, an invented quote, the taste alone and words of liking.
    /// <summary>Any wording of a level passes, because the model reads it; only a level with nothing behind it stops.</summary>
    [Theory]
    [InlineData("tôi thích cay nhẹ", "cay nhẹ", true)]
    [InlineData("tôi thích CAY NHẸ!", "Cay nhẹ", true)]
    [InlineData("toi thich cay nhe", "cay nhẹ", true)]
    [InlineData("cay xé lưỡi luôn nha", "cay xé lưỡi", true)]
    [InlineData("tôi thích cay", null, false)]
    [InlineData("tôi thích cay", "  ", false)]
    [InlineData("tôi thích cay", "cay nhẹ", false)]
    [InlineData("tôi thích cay", "cay", false)]
    [InlineData("tôi thích ăn cay", "thích ăn cay", false)]
    public void Unbacked_NewTarget_NeedsTheUsersWordsForItsLevel(string content, string? quote, bool backed)
    {
        // STEP-01: Get context from Context and create proposed with the target.
        var context = Context(content);
        var proposed = context.State with { TasteTargets = [new("spicy", .25, quote)] };

        // STEP-02: Assert AgentConstraintGuard.UnbackedTastes() is empty or names the dimension.
        AgentConstraintGuard.UnbackedTastes(context, proposed).Should().BeEquivalentTo(backed ? [] : new[] { "spicy" });
    }

    // TEST-02: Words from an earlier message or the request being clarified also back a level, because the answer may come over several turns.
    /// <summary>"Vừa" answering a question is a level even though the current message says something else.</summary>
    [Theory]
    [InlineData("history")]
    [InlineData("pending")]
    public void Unbacked_EarlierWords_BackALevel(string source)
    {
        // STEP-01: Get context from Context and put the words in source.
        var context = Context("cho cả chuyến nhé");
        if (source == "history") context.History.Add(new("user", "Vừa"));
        else context.State = context.State with { PendingPlanningRequest = new(["Vừa"]) };

        // STEP-02: Assert AgentConstraintGuard.UnbackedTastes() is empty.
        AgentConstraintGuard.UnbackedTastes(context, context.State with { TasteTargets = [new("spicy", .5, "Vừa")] }).Should().BeEmpty();
    }

    // TEST-03: The model's own earlier reply never backs a level.
    /// <summary>A level the assistant suggested is not one the user chose.</summary>
    [Fact]
    public void Unbacked_AssistantWords_DoNotBackALevel()
    {
        // STEP-01: Get context from Context and add an assistant message naming the level.
        var context = Context("ok");
        context.History.Add(new("assistant", "Mình để cay nhiều nhé"));

        // STEP-02: Assert AgentConstraintGuard.UnbackedTastes() names the dimension.
        AgentConstraintGuard.UnbackedTastes(context, context.State with { TasteTargets = [new("spicy", .75, "cay nhiều")] }).Should().Equal("spicy");
    }

    // TEST-04: A target already in the state needs no quote, while changing its level or scope does; covers unchanged, another level, a whole trip moved to one day and another meal.
    /// <summary>The model keeps resending state every turn, so only what it changes is checked.</summary>
    [Theory]
    [InlineData("same", false)]
    [InlineData("level", true)]
    [InlineData("day", true)]
    [InlineData("meal", true)]
    public void Unbacked_KnownTarget_NeedsNoQuote(string change, bool unbacked)
    {
        // STEP-01: Get context from Context with a trip target and a meal target.
        var context = Context("đổi món khác");
        context.State = context.State with { TasteTargets = [new("spicy", .5)], TasteOverrides = [new(1, "lunch", [new("sour", .25)])] };
        // STEP-02: Create proposed from change.
        var proposed = change switch
        {
            "level" => context.State with { TasteTargets = [new("spicy", .75)] },
            "day" => context.State with { TasteOverrides = [new(1, null, [new("sour", .25)])] },
            "meal" => context.State with { TasteOverrides = [new(1, "dinner", [new("sour", .25)])] },
            _ => context.State
        };

        // STEP-03: Assert whether AgentConstraintGuard.UnbackedTastes() has items.
        AgentConstraintGuard.UnbackedTastes(context, proposed).Any().Should().Be(unbacked);
    }

    // TEST-05: Nothing is checked with no proposed state or after the user confirmed the change; a proposal with no taste passes.
    /// <summary>A confirmed change was already agreed by the user, so it must not ask again.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("confirmed")]
    [InlineData("tasteless")]
    public void Unbacked_NothingToCheck_IsEmpty(string kind)
    {
        // STEP-01: Get context from Context and set it up from kind.
        var context = Context("xác nhận");
        context.ReplanAfterConfirmation = kind == "confirmed";
        AgentState? proposed = kind switch { "none" => null, "confirmed" => context.State with { TasteTargets = [new("spicy", .75)] }, _ => context.State with { NumDays = 3 } };

        // STEP-02: Assert AgentConstraintGuard.UnbackedTastes() is empty.
        AgentConstraintGuard.UnbackedTastes(context, proposed).Should().BeEmpty();
    }

    // TEST-06: Quotes are dropped from targets and overrides, and a state without any is returned as the same instance.
    /// <summary>A quote stored in state would make the same target look different and force a needless replan.</summary>
    [Fact]
    public void WithoutQuotes_DropsThemEverywhere()
    {
        // STEP-01: Create state with a quote in a target and in an override.
        var state = new AgentState { TasteTargets = [new("spicy", .25, "cay nhẹ")], TasteOverrides = [new(1, null, [new("sour", .5, "chua vừa")])] };

        // STEP-02: Prepare clean through AgentConstraintGuard.WithoutQuotes.
        var clean = AgentConstraintGuard.WithoutQuotes(state);

        // STEP-03: Assert the quotes are gone, the values stay and a clean state is returned as it is.
        clean.TasteTargets.Should().Equal(new AgentTasteTarget("spicy", .25));
        clean.TasteOverrides.Single().Targets.Should().Equal(new AgentTasteTarget("sour", .5));
        AgentConstraintGuard.WithoutQuotes(clean).Should().BeSameAs(clean);
    }

    /// <summary>Builds a context holding the given message over an empty trip.</summary>
    private static AgentContext Context(string content)
    {
        var context = AgentTestData.Context();
        context.State = new AgentState();
        context.Request.Content = content;
        return context;
    }
}
