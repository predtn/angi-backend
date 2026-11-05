using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Intake;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies that LLM-generated consent cannot bypass hard trip constraints.</summary>
public sealed class AgentStatePolicyTests
{
    // TEST-01: The model's state can neither set nor clear the mark that a dish was just offered.
    /// <summary>Only the backend knows it made an offer, and that mark decides how "ok" is read.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(true, null)]
    public void Accept_DishOffered_StaysBackendOwned(bool? current, bool? proposed)
    {
        // STEP-01: Derive state from a copy of AgentTestData.State() and create policy as AgentConstraintGuard.
        var state = AgentTestData.State() with { DishOffered = current };
        var policy = new AgentConstraintGuard();

        // STEP-02: Assert policy.Accept().DishOffered is current.
        policy.Accept(state, state with { DishOffered = proposed }).DishOffered.Should().Be(current);
    }

    // TEST-02: Transient search filters can be removed while removing a confirmed constraint is rejected.
    /// <summary>Ensures temporary search filters can be dropped while confirmed constraints stay enforced.</summary>
    [Fact]
    public void Accept_TransientSearchFilters_CanBeRemovedWithoutRelaxingConfirmedConstraints()
    {
        // STEP-01: Derive confirmed from a copy of AgentTestData.State(), derive temporary from a copy of confirmed and create policy as AgentConstraintGuard.
        var confirmed = AgentTestData.State() with { ExcludeTagIds = [1] };
        var temporary = confirmed with { ExcludeTagIds = [1, 2] };
        var policy = new AgentConstraintGuard();
        // STEP-02: Assert policy.Accept().ExcludeTagIds equals 1 and policy.Accept() throws ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException.
        policy.Accept(temporary, confirmed, confirmed).ExcludeTagIds.Should().Equal((short)1);
        ((Action)(() => policy.Accept(temporary, confirmed with { ExcludeTagIds = [] }, confirmed)))
            .Should().Throw<ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException>();
    }

    // TEST-03: Reject silent constraint relaxation across province, days, budget, tags, radius and origin.
    /// <summary>Protects each hard-constraint equivalence class from model-controlled relaxation.</summary>
    [Theory]
    [InlineData("province")][InlineData("days")][InlineData("budget")][InlineData("budget-null")][InlineData("unrestricted")]
    [InlineData("price")][InlineData("price-null")][InlineData("tag")][InlineData("exclude")][InlineData("radius")][InlineData("origin")]
    public void Accept_RelaxedConstraint_RequiresHumanState(string area)
    {
        // STEP-01: Derive current from a copy of AgentTestData.State() and derive proposed from the expected value.
        var current = AgentTestData.State() with { MaxPricePerMeal = 50000, TagIds = [1], ExcludeTagIds = [2],
            OriginMode = "pinned", OriginLatitude = 16, OriginLongitude = 108, RadiusKm = 10 };
        var proposed = area switch
        {
            "province" => current with { Province = "Other" }, "days" => current with { NumDays = 3 },
            "budget" => current with { BudgetAmount = 500001 }, "budget-null" => current with { BudgetAmount = null },
            "unrestricted" => current with { BudgetUnrestricted = true }, "price" => current with { MaxPricePerMeal = 50001 },
            "price-null" => current with { MaxPricePerMeal = null }, "tag" => current with { TagIds = [] },
            "exclude" => current with { ExcludeTagIds = [] }, "radius" => current with { RadiusKm = 11 },
            _ => current with { OriginLatitude = 17 }
        };
        // STEP-02: Wrap new AgentConstraintGuard().Accept with current and proposed in act so its outcome can be asserted.
        var act = () => new AgentConstraintGuard().Accept(current, proposed);
        // STEP-03: Assert act throws ConflictException and the thrown exception.ErrorCode is "PROPOSAL_NOT_COMMITTABLE".
        act.Should().Throw<ConflictException>().Which.ErrorCode.Should().Be("PROPOSAL_NOT_COMMITTABLE");
    }

    // TEST-04: Allow initial enrichment, unchanged state and stricter monetary constraints without changing preferences globally.
    /// <summary>Verifies safe enrichment and tightening remain functional.</summary>
    [Fact]
    public void Accept_EnrichmentAndTightening_Succeeds()
    {
        // STEP-01: Create policy as AgentConstraintGuard and get current from AgentTestData.State.
        var policy = new AgentConstraintGuard();
        var current = AgentTestData.State();
        // STEP-02: Assert policy.Accept() is the same instance as current and policy.Accept().BudgetAmount is 100000.
        policy.Accept(new AgentState(), current).Should().BeSameAs(current);
        policy.Accept(current, null).Should().BeSameAs(current);
        policy.Accept(current, current with { BudgetAmount = 100000, MaxPricePerMeal = 40000 }).BudgetAmount.Should().Be(100000);
    }

    // TEST-05: The model or a client cannot open, advance or close the pre-plan questions.
    /// <summary>Verifies the intake stage stays backend-owned, like the other pending metadata.</summary>
    [Fact]
    public void Accept_IntakeStage_StaysBackendOwned()
    {
        // STEP-01: Create policy as AgentConstraintGuard and derive current from a copy of AgentTestData.State().
        var policy = new AgentConstraintGuard();
        var current = AgentTestData.State() with { PlanningIntake = AgentIntakePolicy.Details };

        // STEP-02: Assert policy.Accept().PlanningIntake is AgentIntakePolicy.Details and policy.Accept().PlanningIntake is null.
        policy.Accept(current, AgentTestData.State()).PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        policy.Accept(AgentTestData.State(), current).PlanningIntake.Should().BeNull();
    }

    // TEST-06: Day places stay backend-owned: the model can neither drop nor write them; a shorter trip drops later days and a new province drops them all.
    /// <summary>Verifies the itinerary survives every model state, whose schema has no such field, and never outlives the trip it belongs to.</summary>
    [Fact]
    public void Accept_DayAreas_StayBackendOwned()
    {
        // STEP-01: Create policy as AgentConstraintGuard, build the areas collection and derive current from a copy of AgentTestData.State().
        var policy = new AgentConstraintGuard();
        AgentDayArea[] areas = [new(1, "Hoàn Kiếm", 21.03m, 105.85m, 3m), new(2, "Lăng Bác", 21.04m, 105.83m, 3m)];
        var current = AgentTestData.State() with { DayAreas = areas };

        // STEP-02: Assert policy.Accept().DayAreas equals areas and policy.Accept().DayAreas is null.
        policy.Accept(current, AgentTestData.State()).DayAreas.Should().Equal(areas);
        policy.Accept(AgentTestData.State(), current).DayAreas.Should().BeNull();
        policy.Accept(current, current with { BudgetAmount = 100000 }).DayAreas.Should().Equal(areas);
        // STEP-03: Define delegate shorter.
        var shorter = () => policy.Accept(current, AgentTestData.State() with { NumDays = 1 });
        // STEP-04: Assert shorter throws ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException and the thrown exception.Proposed.DayAreas equals areas[0].
        shorter.Should().Throw<ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException>().Which.Proposed.DayAreas.Should().Equal(areas[0]);
        // STEP-05: Define delegate moved.
        var moved = () => policy.Accept(current, AgentTestData.State() with { Province = "Huế" });
        // STEP-06: Assert moved throws ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException and the thrown exception.Proposed.DayAreas is null.
        moved.Should().Throw<ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException>().Which.Proposed.DayAreas.Should().BeNull();
    }

    // TEST-07: The waiting "which day" place stays backend-owned: a model state can neither drop nor write it.
    /// <summary>Verifies a model reply in between cannot lose or invent the place the user named.</summary>
    [Fact]
    public void Accept_PendingDayArea_StaysBackendOwned()
    {
        // STEP-01: Create policy as AgentConstraintGuard, create pending as AgentPlaceMatch and derive current from a copy of AgentTestData.State().
        var policy = new AgentConstraintGuard();
        var pending = new AgentPlaceMatch("Da Nang", "Cầu Giấy", 21.03m, 105.79m, 3m);
        var current = AgentTestData.State() with { PendingDayArea = pending };

        // STEP-02: Assert policy.Accept().PendingDayArea is pending and policy.Accept().PendingDayArea is null.
        policy.Accept(current, AgentTestData.State()).PendingDayArea.Should().Be(pending);
        policy.Accept(AgentTestData.State(), current).PendingDayArea.Should().BeNull();
    }

    // TEST-08: A widened radius stays backend-owned: a model can neither drop nor write it, a shorter trip drops later days, and a new province drops all.
    /// <summary>Verifies only the backend's own search can allow a meal to reach further out.</summary>
    [Fact]
    public void Accept_WidenedMeals_StayBackendOwned()
    {
        // STEP-01: Create policy as AgentConstraintGuard, build the widened collection and derive current from a copy of AgentTestData.State().
        var policy = new AgentConstraintGuard();
        AgentWidenedMeal[] widened = [new(1, "lunch", 8m), new(2, "lunch", 8m)];
        var current = AgentTestData.State() with { WidenedMeals = widened };

        // STEP-02: Assert policy.Accept().WidenedMeals equals widened and policy.Accept().WidenedMeals is null.
        policy.Accept(current, AgentTestData.State()).WidenedMeals.Should().Equal(widened);
        policy.Accept(AgentTestData.State(), current).WidenedMeals.Should().BeNull();
        // STEP-03: Define delegate shorter.
        var shorter = () => policy.Accept(current, AgentTestData.State() with { NumDays = 1 });
        // STEP-04: Assert shorter throws ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException and 1 more.
        shorter.Should().Throw<ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException>().Which.Proposed.WidenedMeals.Should().Equal(widened[0]);
        // STEP-05: Define delegate moved.
        var moved = () => policy.Accept(current, AgentTestData.State() with { Province = "Huế" });
        // STEP-06: Assert moved throws ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException and the thrown exception.Proposed.WidenedMeals is null.
        moved.Should().Throw<ANGI.Application.Common.Exceptions.AgentStateChangeRequiredException>().Which.Proposed.WidenedMeals.Should().BeNull();
    }

    // TEST-09: The named-area label stays backend-owned, so a model or client cannot turn a GPS or map origin into a named area.
    /// <summary>Verifies only the backend's reading of the user's words can switch on the near-first order.</summary>
    [Fact]
    public void Accept_NamedAreaLabel_StaysBackendOwned()
    {
        // STEP-01: Create policy as AgentConstraintGuard and derive current from a copy of AgentTestData.State().
        var policy = new AgentConstraintGuard();
        var current = AgentTestData.State() with { NamedAreaLabel = "Cầu Giấy" };

        // STEP-02: Assert policy.Accept().NamedAreaLabel is the expected text and policy.Accept().NamedAreaLabel is null.
        policy.Accept(current, AgentTestData.State()).NamedAreaLabel.Should().Be("Cầu Giấy");
        policy.Accept(AgentTestData.State(), current).NamedAreaLabel.Should().BeNull();
    }
}
