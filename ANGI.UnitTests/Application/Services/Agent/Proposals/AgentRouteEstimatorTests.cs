using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.Services.Agent.Proposals;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent.Proposals;

/// <summary>Checks preview routes, missing data and the shared RM-14 point cap.</summary>
public sealed class AgentRouteEstimatorTests
{
    // TEST-01: Drop a remote origin and propagate estimated metrics without claiming verified travel.
    /// <summary>Verifies the over-30-km origin rule and route uncertainty metadata.</summary>
    [Fact]
    public async Task EstimateAsync_RemoteOrigin_SkipsAndPreservesEstimate()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator();
        // STEP-02: Stub fixture.CalculateRouteAsync.
        fixture.Routes.Setup(x => x.CalculateRouteAsync(null, It.IsAny<IReadOnlyList<RouteCoordinate>>(), RouteProfile.Driving, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouteCalculationResult([], true));
        // STEP-03: Derive state from a copy of AgentTestData.State() and get result from new AgentRouteEstimator().EstimateAsync.
        var state = AgentTestData.State() with { OriginMode = "pinned", OriginLatitude = 10, OriginLongitude = 106 };
        var result = await new AgentRouteEstimator(fixture.Routes.Object).EstimateAsync(state, 1,
            [AgentTestData.Item() with { Latitude = 16, Longitude = 108 }], RouteProfile.Driving, CancellationToken.None);
        // STEP-04: Assert result.OriginSkipped is true and result.IsEstimated is true.
        result.OriginSkipped.Should().BeTrue(); result.IsEstimated.Should().BeTrue();
    }

    // TEST-02: Reject missing or out-of-range coordinates and more than 24 destinations before provider calls.
    /// <summary>Prevents missing data from being reported as a validated route.</summary>
    [Theory]
    [InlineData("missing")][InlineData("latitude")][InlineData("longitude")][InlineData("many")]
    public async Task EstimateAsync_InvalidPoints_Rejects(string area)
    {
        // STEP-01: Derive item from a copy of AgentTestData.Item(), derive items from the expected value and create fixture through AgentTestData.Validator.
        var item = AgentTestData.Item() with { Latitude = 16, Longitude = 108 };
        var items = area switch { "missing" => [item with { Latitude = null }], "latitude" => [item with { Latitude = 91 }],
            "longitude" => [item with { Longitude = 181 }], _ => Enumerable.Repeat(item, 25).ToArray() };
        var fixture = AgentTestData.Validator();
        // STEP-02: Wrap new AgentRouteEstimator().EstimateAsync with items in act so its outcome can be asserted.
        var act = () => new AgentRouteEstimator(fixture.Routes.Object).EstimateAsync(AgentTestData.State(), 1, items, RouteProfile.Driving, CancellationToken.None);
        // STEP-03: Assert act throws ConflictException.
        await act.Should().ThrowAsync<ConflictException>();
        // STEP-04: Verify fixture receives no other calls.
        fixture.Routes.VerifyNoOtherCalls();
    }

    // TEST-03: Reject malformed provider legs rather than trusting a successful provider response.
    /// <summary>Checks route-result cardinality before a preview can become ready.</summary>
    [Fact]
    public async Task EstimateAsync_WrongLegCount_Rejects()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator();
        // STEP-02: Stub fixture.CalculateRouteAsync.
        fixture.Routes.Setup(x => x.CalculateRouteAsync(It.IsAny<RouteCoordinate?>(), It.IsAny<IReadOnlyList<RouteCoordinate>>(), RouteProfile.Driving, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouteCalculationResult([new(1, 1)], false));
        // STEP-03: Wrap new AgentRouteEstimator().EstimateAsync in act so its outcome can be asserted.
        var act = () => new AgentRouteEstimator(fixture.Routes.Object).EstimateAsync(AgentTestData.State(), 1,
            [AgentTestData.Item() with { Latitude = 16, Longitude = 108 }], RouteProfile.Driving, CancellationToken.None);
        // STEP-04: Assert act throws ServiceUnavailableException.
        await act.Should().ThrowAsync<ServiceUnavailableException>();
    }
}
