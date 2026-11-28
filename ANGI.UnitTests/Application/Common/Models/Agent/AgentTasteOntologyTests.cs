using ANGI.Application.Common.Models.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Agent;

/// <summary>Verifies how stated tastes shape the ranking vector.</summary>
public sealed class AgentTasteOntologyTests
{
    // Profile of conversation 109: spicy 0.5, cooling 1, fresh 0.875. "muốn ăn cay" used to leave the vector unchanged and planned steamed fish and soups.
    private static readonly double[] Profile = [0.5, 0, 0.5, 0, 1, 0.5, 1, 0, 0.25, 0.875, 1, 1];

    // TEST-01: A wanted taste leads: it never sits below the profile, and the tastes the user did not mention count half.
    /// <summary>Spicy 0.75 on a profile at 0.5 becomes 0.75 and every other dimension is halved, so spicy is the strongest one.</summary>
    [Fact]
    public void Apply_WantedTaste_LeadsOverUnmentionedOnes()
    {
        var vector = AgentTasteOntology.Apply(new() { Initialized = true, BaselineVector = Profile }, [new("spicy", 0.75)]).EffectiveVector!;

        vector[5].Should().Be(0.75);
        vector[6].Should().Be(0.5);
        vector[9].Should().Be(0.4375);
        vector.Max().Should().Be(vector[5]);
    }

    // TEST-02: A wanted taste already strong in the profile is not lowered by the default level; a taste the user does not want is used as stated.
    /// <summary>Umami 0.75 on a profile at 1 stays 1; sweet 0 ("không ngọt") becomes 0.</summary>
    [Fact]
    public void Apply_KeepsStrongProfileTasteAndUnwantedTaste()
    {
        var vector = AgentTasteOntology.Apply(new() { Initialized = true, BaselineVector = Profile }, [new("umami", 0.75), new("sweet", 0)]).EffectiveVector!;

        vector[4].Should().Be(1);
        vector[0].Should().Be(0);
    }

    // TEST-03: With no stated taste the profile is used as it is.
    /// <summary>No targets leave every dimension unchanged.</summary>
    [Fact]
    public void Apply_NoTargets_KeepsProfile()
    {
        AgentTasteOntology.Apply(new() { Initialized = true, BaselineVector = Profile }, []).EffectiveVector.Should().Equal(Profile);
    }
}
