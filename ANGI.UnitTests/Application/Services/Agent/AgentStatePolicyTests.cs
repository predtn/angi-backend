using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies that LLM-generated consent cannot bypass hard trip constraints.</summary>
public sealed class AgentStatePolicyTests
{
    // TEST-01: Reject silent constraint relaxation across province, days, budget, tags, radius and origin.
    /// <summary>Protects each hard-constraint equivalence class from model-controlled relaxation.</summary>
    [Theory]
    [InlineData("province")][InlineData("days")][InlineData("budget")][InlineData("budget-null")][InlineData("unrestricted")]
    [InlineData("price")][InlineData("price-null")][InlineData("tag")][InlineData("exclude")][InlineData("radius")][InlineData("origin")]
    public void Accept_RelaxedConstraint_RequiresHumanState(string area)
    {
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
        var act = () => new AgentStatePolicy().Accept(current, proposed);
        act.Should().Throw<ConflictException>().Which.ErrorCode.Should().Be("PROPOSAL_NOT_COMMITTABLE");
    }

    // TEST-02: Allow initial enrichment, unchanged state and stricter monetary constraints without changing preferences globally.
    /// <summary>Verifies safe enrichment and tightening remain functional.</summary>
    [Fact]
    public void Accept_EnrichmentAndTightening_Succeeds()
    {
        var policy = new AgentStatePolicy();
        var current = AgentTestData.State();
        policy.Accept(new AgentState(), current).Should().BeSameAs(current);
        policy.Accept(current, null).Should().BeSameAs(current);
        policy.Accept(current, current with { BudgetAmount = 100000, MaxPricePerMeal = 40000 }).BudgetAmount.Should().Be(100000);
    }
}
