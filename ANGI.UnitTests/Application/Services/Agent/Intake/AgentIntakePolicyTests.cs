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

    // TEST-04: Tools close only for a new plan with no special request whose search covered every meal; covers each reason to keep them open.
    /// <summary>Ensures the saving from conversation 94 never costs a side dish or named request its extra search.</summary>
    [Theory]
    [InlineData("covered", true)][InlineData("not-intake", false)][InlineData("no-meals", false)][InlineData("meal-request", false)]
    [InlineData("gap", false)][InlineData("uncovered-day", false)][InlineData("taste-answer", false)][InlineData("side-dish", false)][InlineData("earlier-request", false)]
    [InlineData("quick-reply", true)][InlineData("taste-only", true)][InlineData("sightseeing", true)]
    public void PlanCovered_OnlyWithoutAnythingLeftToSearch(string area, bool expected)
    {
        // STEP-01: Get context from Context, set context.IntakeComplete to area != "not-intake", set context.State to a copy of context.State and 2 more.
        var context = Context(area switch { "taste-answer" => "không ăn cay", "side-dish" => "thêm trà sữa nhé", "quick-reply" => "Không, lập lịch luôn nhé.", "taste-only" => "Cay nhẹ cho toàn chuyến đi", _ => "không" });
        context.IntakeComplete = area != "not-intake";
        context.State = context.State with
        {
            NumDays = 2, MealSlots = area == "no-meals" ? [] : ["lunch"],
            MealRequests = area == "meal-request" ? [new(1, "lunch", "Phở", null, [])] : [],
            PendingPlanningRequest = area == "earlier-request" ? new(["ngày 1 ăn hải sản"]) : area == "sightseeing" ? new(["Ngày 1 tôi đi tham quan Hồ Gươm ở quận Hoàn Kiếm"]) : null
        };
        context.MealCoverage.Add(new(1, "lunch", area == "gap" ? 0 : 3, "", []));
        if (area != "uncovered-day") context.MealCoverage.Add(new(2, "lunch", 3, "", []));

        // STEP-02: Assert AgentIntakePolicy.PlanCovered() is expected.
        AgentIntakePolicy.PlanCovered(context).Should().Be(expected);
    }

    // TEST-05: The preferences question is due only while the trip is settled, it was not asked yet and the state holds no preference; covers each condition, reading the state and never the wording.
    /// <summary>Keeps the question before the plan without asking it twice or asking it too early.</summary>
    [Theory]
    [InlineData(null, 3, null, true, "none", true)]
    [InlineData(null, 3, 500000d, false, "none", true)]
    [InlineData(AgentIntakePolicy.Details, 3, 500000d, false, "none", true)]
    [InlineData(AgentIntakePolicy.Preferences, 3, 500000d, false, "none", false)]
    [InlineData(null, null, 500000d, false, "none", false)]
    [InlineData(null, 3, null, false, "none", false)]
    [InlineData(null, 3, null, true, "taste", false)]
    [InlineData(null, 3, null, true, "override", false)]
    [InlineData(null, 3, null, true, "meal", false)]
    [InlineData(null, 3, null, true, "tag", false)]
    [InlineData(null, 3, null, true, "price", false)]
    public void PreferencesDue_ReadsOnlyTheState(string? stage, int? days, double? budget, bool unrestricted, string preference, bool expected)
    {
        // STEP-01: Get context from Context with a message naming a dish, which must not count, and set context.State.
        var context = Context("gợi ý món ăn bữa tối ăn hải sản");
        context.CatalogProvinces = Provinces;
        context.State = context.State with { Province = "Hà Nội", NumDays = (short?)days, BudgetAmount = (decimal?)budget, BudgetUnrestricted = unrestricted, PlanningIntake = stage,
            TasteTargets = preference == "taste" ? [new("spicy", 0.5)] : [], TasteOverrides = preference == "override" ? [new(1, null, [new("spicy", 0.5)])] : [],
            MealRequests = preference == "meal" ? [new(1, "lunch", "Phở", null, [])] : [], TagIds = preference == "tag" ? [(short)1] : [], MaxPricePerMeal = preference == "price" ? 50000 : null };

        // STEP-02: Assert AgentIntakePolicy.PreferencesDue() is expected.
        AgentIntakePolicy.PreferencesDue(context).Should().Be(expected);
    }

    // TEST-06: A trip submitted through the form plans at once, while the location button still leads to the question.
    /// <summary>The form states the whole trip; the button only answers where.</summary>
    [Theory]
    [InlineData("none", false)]
    [InlineData("pinned", false)]
    [InlineData("gps", true)]
    public void PreferencesDue_ConfirmedState_OnlyLocationKeepsTheQuestion(string originMode, bool expected)
    {
        // STEP-01: Get context from Context with a settled trip and the confirmed state.
        var context = Context("Cập nhật điều kiện");
        context.CatalogProvinces = Provinces;
        context.State = new AgentState { Province = "Hà Nội", NumDays = 3, BudgetAmount = 500000 };
        context.Request.ConfirmedState = context.State with { OriginMode = originMode };

        // STEP-02: Assert AgentIntakePolicy.PreferencesDue() is expected.
        AgentIntakePolicy.PreferencesDue(context).Should().Be(expected);
    }

    // TEST-07: An existing plan or a replan the user already confirmed never brings the question back.
    /// <summary>The question belongs to new plans only.</summary>
    [Fact]
    public void PreferencesDue_ExistingPlanOrConfirmedReplan_IsFalse()
    {
        // STEP-01: Create settled as a trip with every detail known.
        var settled = new AgentState { Province = "Hà Nội", NumDays = 3, BudgetAmount = 500000 };
        var plan = Context("ok", AgentTestData.Snapshot(AgentTestData.Item()));
        plan.State = settled;
        plan.CatalogProvinces = Provinces;
        var replan = Context("ok");
        replan.State = settled;
        replan.CatalogProvinces = Provinces;
        replan.ReplanAfterConfirmation = true;

        // STEP-02: Assert AgentIntakePolicy.PreferencesDue() is false for both.
        AgentIntakePolicy.PreferencesDue(plan).Should().BeFalse();
        AgentIntakePolicy.PreferencesDue(replan).Should().BeFalse();
    }

    // TEST-08: With no plan the baseline frees the place, the duration and an unset budget, while a budget already set stays protected.
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

    // TEST-09: A province the model read takes the catalog's spelling; one ANGI has no data for is dropped and remembered; covers accents, a short name, no spaces, an unknown name, no list and no province.
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

    // TEST-10: Nothing is checked when no list was loaded or no province was stated.
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

    // TEST-11: The preferences question says when the plan has no budget and stays plain when it has one, and records the stage and the question.
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
