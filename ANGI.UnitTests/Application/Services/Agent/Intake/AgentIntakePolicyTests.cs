using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Intake;

/// <summary>Verifies AgentIntakePolicy guards the order of the questions before a new plan from the state alone.</summary>
public sealed class AgentIntakePolicyTests
{
    private static readonly string[] Provinces = ["Hà Nội", "TP. Hồ Chí Minh"];

    // TEST-01: A catalog province is matched by whole words; covers accents, no accents, short names and a word that only contains the name.
    /// <summary>Ensures short names resolve while unrelated text is never mistaken for a place.</summary>
    [Theory]
    [InlineData("ha noi", "Hà Nội")][InlineData("toi o hn", "Hà Nội")][InlineData("di sai gon", "TP. Hồ Chí Minh")][InlineData("tp hcm", "TP. Hồ Chí Minh")]
    [InlineData("ha noii", null)][InlineData("da nang", null)]
    public void FindProvince_MatchesWholeWordsAndAliases(string text, string? expected) =>
        // STEP-01: Assert AgentIntakePolicy.FindProvince() is expected.
        AgentIntakePolicy.FindProvince(text, Provinces).Should().Be(expected);

    // TEST-02: A plan on screen, a staged change or a non-chat command all keep the existing flow.
    /// <summary>Ensures editing an existing plan never restarts the questions.</summary>
    [Fact]
    public void Eligible_ExistingPlanStagedChangeOrCommand_IsFalse()
    {
        // STEP-01: Assert AgentIntakePolicy.Eligible() is true and AgentIntakePolicy.Eligible() is false.
        AgentIntakePolicy.Eligible(Context("lập lịch")).Should().BeTrue();
        AgentIntakePolicy.Eligible(Context("lập lịch", AgentTestData.Snapshot(AgentTestData.Item()))).Should().BeFalse();
        AgentIntakePolicy.Eligible(Context("lập lịch", AgentTestData.Snapshot())).Should().BeTrue();
        // STEP-02: Get staged from Context and set staged.State to a copy of staged.State.
        var staged = Context("lập lịch");
        staged.State = staged.State with { PendingStateChange = new AgentState() };
        // STEP-03: Assert AgentIntakePolicy.Eligible() is false.
        AgentIntakePolicy.Eligible(staged).Should().BeFalse();
        // STEP-04: Get command from Context and set command.Request.Command to "reset".
        var command = Context("lập lịch");
        command.Request.Command = "reset";
        // STEP-05: Assert AgentIntakePolicy.Eligible() is false.
        AgentIntakePolicy.Eligible(command).Should().BeFalse();
    }

    // TEST-03: A province only the roadmap title holds is dropped from a model's state; covers the title, a stated name, a known place, coordinates, another place and a null state.
    /// <summary>Ensures "3 ngày ăn ngon Hà Nội" in the title cannot answer the place question on the user's behalf.</summary>
    [Fact]
    public void WithoutUnstatedProvince_KeepsOnlyUserStatedPlace()
    {
        // STEP-01: Create titled as AgentState and define helper Titled.
        var titled = new AgentState { Province = "Hà Nội", Title = "3 ngày ăn ngon Hà Nội" };
        AgentContext Titled(string content)
        {
            var context = Context(content);
            context.State = context.State with { Title = titled.Title };
            return context;
        }
        // STEP-02: Assert WithoutUnstatedProvince().Province is null, WithoutUnstatedProvince().Province is the expected text and 1 more.
        AgentIntakePolicy.WithoutUnstatedProvince(titled, Titled("lập lịch cho tôi"))!.Province.Should().BeNull();
        AgentIntakePolicy.WithoutUnstatedProvince(titled, Titled("lập lịch ở ha noi"))!.Province.Should().Be("Hà Nội");
        AgentIntakePolicy.WithoutUnstatedProvince(titled with { Province = "Huế" }, Titled("lập lịch"))!.Province.Should().Be("Huế");
        AgentIntakePolicy.WithoutUnstatedProvince(null, Context("lập lịch")).Should().BeNull();
        // STEP-03: Get history from Titled and add the computed value to history.History.
        var history = Titled("3 ngày nhé");
        history.History.Add(new("user", "Hà Nội"));
        // STEP-04: Assert WithoutUnstatedProvince().Province is the expected text.
        AgentIntakePolicy.WithoutUnstatedProvince(titled, history)!.Province.Should().Be("Hà Nội");
        // STEP-05: Get known from Titled and set known.State to a copy of known.State.
        var known = Titled("lập lịch");
        known.State = known.State with { Province = "Hà Nội" };
        // STEP-06: Assert AgentIntakePolicy.WithoutUnstatedProvince() is the same instance as titled.
        AgentIntakePolicy.WithoutUnstatedProvince(titled, known).Should().BeSameAs(titled);
        // STEP-07: Get located from Titled and set located.State to a copy of located.State.
        var located = Titled("lập lịch");
        located.State = located.State with { OriginMode = "gps", OriginLatitude = 21m, OriginLongitude = 105m };
        // STEP-08: Assert AgentIntakePolicy.WithoutUnstatedProvince() is the same instance as titled.
        AgentIntakePolicy.WithoutUnstatedProvince(titled, located).Should().BeSameAs(titled);
    }

    // TEST-04: With no plan the baseline frees the place, the duration and an unset budget, while a budget already set stays protected.
    /// <summary>The model must not drop a limit the user gave, even before a plan exists.</summary>
    [Fact]
    public void Baseline_FreesOnlyWhatCannotLoseAnything()
    {
        // STEP-01: Create state with every value, and open with no budget.
        var state = new AgentState { Province = "Hà Nội", NumDays = 3, BudgetAmount = 500000, MaxPricePerMeal = 50000 };
        var open = state with { BudgetAmount = null };

        // STEP-02: Assert the baseline values.
        AgentIntakePolicy.Baseline(state).Should().Match<AgentState>(x => x.Province == null && x.NumDays == null && x.BudgetAmount == 500000 && !x.BudgetUnrestricted && x.MaxPricePerMeal == 50000);
        AgentIntakePolicy.Baseline(open).BudgetUnrestricted.Should().BeTrue();
    }

    // TEST-05: A province the model read takes the catalog's spelling; one ANGI has no data for is dropped and remembered; covers accents, a short name, no spaces, an unknown name, no list and no province.
    /// <summary>A trip must never start somewhere with no restaurants, and a spelling difference must not look like one.</summary>
    [Theory]
    [InlineData("ha noi", "Hà Nội", null)]
    [InlineData("HN", "Hà Nội", null)]
    [InlineData("Hanoi", "Hà Nội", null)]
    [InlineData("Đà Nẵng", null, "Đà Nẵng")]
    public void WithCatalogProvince_NormalizesOrDrops(string province, string? kept, string? unknown)
    {
        // STEP-01: Get context from Context and set the catalog's provinces.
        var context = Context("lập lịch");
        context.CatalogProvinces = Provinces;

        // STEP-02: Prepare result through AgentIntakePolicy.WithCatalogProvince.
        var result = AgentIntakePolicy.WithCatalogProvince(new AgentState { Province = province }, context);

        // STEP-03: Assert result.Province is kept and context.UnknownPlace is unknown.
        result!.Province.Should().Be(kept);
        context.UnknownPlace.Should().Be(unknown);
    }

    // TEST-06: Nothing is checked when no list was loaded or no province was stated.
    /// <summary>An existing plan loads no list, and a state without a province has nothing to check.</summary>
    [Fact]
    public void WithCatalogProvince_NoListOrNoProvince_ReturnsSameState()
    {
        // STEP-01: Create state and get context from Context.
        var state = new AgentState { Province = "Đà Nẵng" };
        var context = Context("lập lịch");

        // STEP-02: Assert the same instances come back and nothing is remembered.
        AgentIntakePolicy.WithCatalogProvince(state, context).Should().BeSameAs(state);
        context.CatalogProvinces = Provinces;
        AgentIntakePolicy.WithCatalogProvince(null, context).Should().BeNull();
        context.UnknownPlace.Should().BeNull();
    }

    // TEST-07: The preferences question says when the plan has no budget and stays plain when it has one, and records the stage and the question.
    /// <summary>The user must learn there is no limit before the plan is made, and the plan-now button depends on the stage.</summary>
    [Theory]
    [InlineData(true, null, true)][InlineData(false, 500000d, false)]
    public void AskPreferences_StatesMissingBudget(bool unrestricted, double? budget, bool noted)
    {
        // STEP-01: Get step from AgentIntakePolicy.AskPreferences.
        var step = AgentIntakePolicy.AskPreferences(new AgentState { Province = "Hà Nội", NumDays = 3, BudgetUnrestricted = unrestricted, BudgetAmount = (decimal?)budget });

        // STEP-02: Assert step.Reply is the expected value and step.State matches the condition.
        step.Reply.Should().Be(noted ? $"{AgentReplyBuilder.NoBudgetNote} {AgentIntakePolicy.PreferencesQuestion}" : AgentIntakePolicy.PreferencesQuestion);
        step.State.Should().Match<AgentState>(x => x.PlanningIntake == AgentIntakePolicy.Preferences && x.PendingQuestion == step.Reply);
    }

    /// <summary>Builds a context with no plan and an unknown place.</summary>
    private static AgentContext Context(string content, AgentSnapshot? source = null)
    {
        var context = AgentTestData.Context(source);
        context.State = new AgentState();
        context.Request.Content = content;
        return context;
    }
}
