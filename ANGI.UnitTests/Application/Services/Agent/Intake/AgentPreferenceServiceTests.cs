using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.UseCases.Validators.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent.Intake;

/// <summary>Checks profile inheritance, transient failures and ownership of computed vectors.</summary>
public sealed class AgentPreferenceServiceTests
{
    // TEST-01: The reco profile is inherited once as the baseline and later turns change only the dimensions the user stated explicitly.
    /// <summary>Ensures the reco profile seeds preferences once and only dimensions the user stated are overridden.</summary>
    [Fact]
    public async Task EnsureAsync_InheritsOnceAndOnlyChangesExplicitDimensions()
    {
        // STEP-01: Build the profile collection.
        var profile = new double[] { .1, .2, .3, 0, .5, .25, 0, 0, 1.4, 0, -.2, .5 };
        // STEP-02: Mock IRecommendationService; stub reco.GetPreferencesAsync.
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserPreferenceSnapshot(3, 1, profile, DateTime.UtcNow));
        // STEP-03: Create the AgentPreferenceService under test as service.
        var service = new AgentPreferenceService(reco.Object);
        // STEP-04: Run service.EnsureAsync and keep state.
        var state = await service.EnsureAsync(new() { TasteTargets = [new("spicy", .75)] }, 3, default);
        // STEP-05: Assert state.Preferences.EffectiveVector[5] is .75, state.Preferences.EffectiveVector[8] is 1.4 and 2 more.
        state.Preferences!.EffectiveVector![5].Should().Be(.75);
        state.Preferences.EffectiveVector[8].Should().Be(1.4);
        state.Preferences.EffectiveVector[10].Should().Be(-.2);
        profile[5].Should().Be(.25);
        // STEP-06: Set profile[5] to 1 and set state to service.EnsureAsync().
        profile[5] = 1;
        state = await service.EnsureAsync(state with { TasteTargets = [new("spicy", .5)] }, 3, default);
        // STEP-07: Assert state.Preferences.BaselineVector[5] is .25 and state.Preferences.EffectiveVector[5] is .5.
        state.Preferences!.BaselineVector![5].Should().Be(.25);
        state.Preferences.EffectiveVector![5].Should().Be(.5);
        // STEP-08: Verify GetPreferencesAsync is called once.
        reco.Verify(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: An unavailable reco profile keeps the taste targets pending and is retried on the next turn.
    /// <summary>Ensures a temporary reco outage neither loses taste targets nor permanently skips profile inheritance.</summary>
    [Fact]
    public async Task EnsureAsync_UnavailableRetainsTargetsAndRetries()
    {
        // STEP-01: Mock IRecommendationService; stub reco.GetPreferencesAsync.
        var reco = new Mock<IRecommendationService>();
        reco.SetupSequence(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserPreferenceSnapshot?)null)
            .ReturnsAsync(new UserPreferenceSnapshot(3, 1, null, null));
        // STEP-02: Create the AgentPreferenceService under test as service.
        var service = new AgentPreferenceService(reco.Object);
        // STEP-03: Run service.EnsureAsync and keep state.
        var state = await service.EnsureAsync(new() { TasteTargets = [new("spicy", .75)] }, 3, default);
        // STEP-04: Assert state.Preferences.Initialized is false and state.TasteTargets has exactly one matching item.
        state.Preferences!.Initialized.Should().BeFalse();
        state.TasteTargets.Should().ContainSingle();
        // STEP-05: Set state to service.EnsureAsync().
        state = await service.EnsureAsync(state, 3, default);
        // STEP-06: Assert state.Preferences.Initialized is true and state.Preferences.EffectiveVector[5] is .75.
        state.Preferences!.Initialized.Should().BeTrue();
        state.Preferences.EffectiveVector![5].Should().Be(.75);
    }

    // TEST-03: A forged baseline vector from the model is rejected and the effective vector is recomputed from the trusted snapshot.
    /// <summary>Prevents model output from replacing the trusted baseline vector used for ranking.</summary>
    [Fact]
    public void Accept_RejectsForgedBaselineAndRecomputesFromTrustedSnapshot()
    {
        // STEP-01: Build the baseline collection, set baseline[5] to .25, create current as AgentState, derive proposed from a copy of current and 1 more.
        var baseline = new double[12]; baseline[5] = .25;
        var current = new AgentState { Preferences = new() { Initialized = true, BaselineVector = baseline } };
        var proposed = current with { TasteTargets = [new("sweet", .5)], Preferences = new()
            { Initialized = true, BaselineVector = Enumerable.Repeat(1d, 12).ToArray() } };
        var accepted = new AgentConstraintGuard().Accept(current, proposed);
        // STEP-02: Assert accepted.Preferences.EffectiveVector[0] is .5, accepted.Preferences.EffectiveVector[5] is .25 and 1 more.
        accepted.Preferences!.EffectiveVector![0].Should().Be(.5);
        accepted.Preferences.EffectiveVector[5].Should().Be(.25);
        accepted.Preferences.BaselineVector.Should().BeSameAs(baseline);
    }

    // TEST-04: An incompatible reco profile leaves preferences uninitialized; covers a wrong ontology version, another user, a wrong vector length, NaN and infinity.
    /// <summary>Ensures a profile that cannot be trusted is never used as the preference baseline.</summary>
    [Theory]
    [InlineData("wrong-ontology")]
    [InlineData("wrong-user")]
    [InlineData("wrong-length")]
    [InlineData("nan")]
    [InlineData("infinity")]
    public async Task EnsureAsync_IncompatibleProfileStaysPending(string invalid)
    {
        // STEP-01: Build the vector collection and set vector[0] to a test value.
        var vector = new double[invalid == "wrong-length" ? 11 : 12];
        vector[0] = invalid == "nan" ? double.NaN : invalid == "infinity" ? double.PositiveInfinity : .5;
        // STEP-02: Mock IRecommendationService; stub reco.GetPreferencesAsync.
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(
            new UserPreferenceSnapshot(invalid == "wrong-user" ? 4 : 3, invalid == "wrong-ontology" ? 2 : 1, vector, null));
        // STEP-03: Get state from new AgentPreferenceService().EnsureAsync.
        var state = await new AgentPreferenceService(reco.Object).EnsureAsync(new() { TasteTargets = [new("spicy", 1)] }, 3, default);
        // STEP-04: Assert state.Preferences.Initialized is false, state.Preferences.BaselineVector is null and state.TasteTargets has exactly one matching item.
        state.Preferences!.Initialized.Should().BeFalse(); state.Preferences.BaselineVector.Should().BeNull();
        state.TasteTargets.Should().ContainSingle();
    }

    // TEST-05: An empty reco profile initializes preferences with a null effective vector and records when the profile was read.
    /// <summary>Ensures a user without a profile is marked initialized without inventing a vector.</summary>
    [Fact]
    public async Task EnsureAsync_EmptyProfileProducesNullEffectiveVectorAndRecordsReadTime()
    {
        // STEP-01: Mock IRecommendationService; stub reco.GetPreferencesAsync.
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(new UserPreferenceSnapshot(3, 1, null, null));
        // STEP-02: Derive before from DateTime.UtcNow and get state from new AgentPreferenceService().EnsureAsync.
        var before = DateTime.UtcNow;
        var state = await new AgentPreferenceService(reco.Object).EnsureAsync(new(), 3, default);
        // STEP-03: Assert state.Preferences.Initialized is true, state.Preferences.EffectiveVector is null and state.Preferences.ProfileReadAt be on or after before.
        state.Preferences!.Initialized.Should().BeTrue(); state.Preferences.EffectiveVector.Should().BeNull();
        state.Preferences.ProfileReadAt.Should().BeOnOrAfter(before);
        // STEP-04: Set state to EnsureAsync().
        state = await new AgentPreferenceService(reco.Object).EnsureAsync(state with { TasteTargets = [new("spicy", 0)] }, 3, default);
        // STEP-05: Assert state.Preferences.EffectiveVector is null.
        state.Preferences!.EffectiveVector.Should().BeNull();
    }

    // TEST-06: The state validator rejects unknown taste dimensions and unsupported target values; covers an unknown dimension, an off-step target (0.4) and a target above 1.
    /// <summary>Ensures only known taste dimensions and supported intensity steps reach ranking.</summary>
    [Theory]
    [InlineData("unknown", .5)]
    [InlineData("spicy", .4)]
    [InlineData("spicy", 1.1)]
    public void Validate_RejectsUnknownDimensionsAndUnsupportedTargets(string dimension, double target)
    {
        // STEP-01: Assert the validator.Validate().IsValid is false.
        new AgentStateValidator().Validate(new AgentState { TasteTargets = [new(dimension, target)] })
            .IsValid.Should().BeFalse();
    }
}
