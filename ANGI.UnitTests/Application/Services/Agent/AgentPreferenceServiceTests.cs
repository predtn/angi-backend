using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Services.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Checks profile inheritance, transient failures and ownership of computed vectors.</summary>
public sealed class AgentPreferenceServiceTests
{
    [Fact]
    public async Task EnsureAsync_InheritsOnceAndOnlyChangesExplicitDimensions()
    {
        var profile = new double[] { .1, .2, .3, 0, .5, .25, 0, 0, 1.4, 0, -.2, .5 };
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserPreferenceSnapshot(3, 1, profile, DateTime.UtcNow));
        var service = new AgentPreferenceService(reco.Object);
        var state = await service.EnsureAsync(new() { TasteTargets = [new("spicy", .75)] }, 3, default);
        state.Preferences!.EffectiveVector![5].Should().Be(.75);
        state.Preferences.EffectiveVector[8].Should().Be(1.4);
        state.Preferences.EffectiveVector[10].Should().Be(-.2);
        profile[5].Should().Be(.25);
        profile[5] = 1;
        state = await service.EnsureAsync(state with { TasteTargets = [new("spicy", .5)] }, 3, default);
        state.Preferences!.BaselineVector![5].Should().Be(.25);
        state.Preferences.EffectiveVector![5].Should().Be(.5);
        reco.Verify(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureAsync_UnavailableRetainsTargetsAndRetries()
    {
        var reco = new Mock<IRecommendationService>();
        reco.SetupSequence(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserPreferenceSnapshot?)null)
            .ReturnsAsync(new UserPreferenceSnapshot(3, 1, null, null));
        var service = new AgentPreferenceService(reco.Object);
        var state = await service.EnsureAsync(new() { TasteTargets = [new("spicy", .75)] }, 3, default);
        state.Preferences!.Initialized.Should().BeFalse();
        state.TasteTargets.Should().ContainSingle();
        state = await service.EnsureAsync(state, 3, default);
        state.Preferences!.Initialized.Should().BeTrue();
        state.Preferences.EffectiveVector![5].Should().Be(.75);
    }

    [Fact]
    public void Accept_RejectsForgedBaselineAndRecomputesFromTrustedSnapshot()
    {
        var baseline = new double[12]; baseline[5] = .25;
        var current = new AgentState { Preferences = new() { Initialized = true, BaselineVector = baseline } };
        var proposed = current with { TasteTargets = [new("sweet", .5)], Preferences = new()
            { Initialized = true, BaselineVector = Enumerable.Repeat(1d, 12).ToArray() } };
        var accepted = new AgentStatePolicy().Accept(current, proposed);
        accepted.Preferences!.EffectiveVector![0].Should().Be(.5);
        accepted.Preferences.EffectiveVector[5].Should().Be(.25);
        accepted.Preferences.BaselineVector.Should().BeSameAs(baseline);
    }

    [Theory]
    [InlineData("wrong-ontology")]
    [InlineData("wrong-user")]
    [InlineData("wrong-length")]
    [InlineData("nan")]
    [InlineData("infinity")]
    public async Task EnsureAsync_IncompatibleProfileStaysPending(string invalid)
    {
        var vector = new double[invalid == "wrong-length" ? 11 : 12];
        vector[0] = invalid == "nan" ? double.NaN : invalid == "infinity" ? double.PositiveInfinity : .5;
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(
            new UserPreferenceSnapshot(invalid == "wrong-user" ? 4 : 3, invalid == "wrong-ontology" ? 2 : 1, vector, null));
        var state = await new AgentPreferenceService(reco.Object).EnsureAsync(new() { TasteTargets = [new("spicy", 1)] }, 3, default);
        state.Preferences!.Initialized.Should().BeFalse(); state.Preferences.BaselineVector.Should().BeNull();
        state.TasteTargets.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureAsync_EmptyProfileProducesNullEffectiveVectorAndRecordsReadTime()
    {
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.GetPreferencesAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(new UserPreferenceSnapshot(3, 1, null, null));
        var before = DateTime.UtcNow;
        var state = await new AgentPreferenceService(reco.Object).EnsureAsync(new(), 3, default);
        state.Preferences!.Initialized.Should().BeTrue(); state.Preferences.EffectiveVector.Should().BeNull();
        state.Preferences.ProfileReadAt.Should().BeOnOrAfter(before);
        state = await new AgentPreferenceService(reco.Object).EnsureAsync(state with { TasteTargets = [new("spicy", 0)] }, 3, default);
        state.Preferences!.EffectiveVector.Should().BeNull();
    }

    [Theory]
    [InlineData("unknown", .5)]
    [InlineData("spicy", .4)]
    [InlineData("spicy", 1.1)]
    public void Validate_RejectsUnknownDimensionsAndUnsupportedTargets(string dimension, double target)
    {
        new AgentStateValidator().Validate(new AgentState { TasteTargets = [new(dimension, target)] })
            .IsValid.Should().BeFalse();
    }
}
