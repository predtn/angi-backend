using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.Services.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent.Places;

/// <summary>
/// Verifies a whole-trip zone of several places ("nội đô") is never treated as its first place: not as the start of a day, a route or a roadmap pin,
/// and it survives a model copying its point back rounded and a roadmap edited outside the chat.
/// </summary>
public sealed class AgentZoneOriginTests
{
    private static readonly AgentZoneCircle[] Zone = [new("Hoàn Kiếm", 21.0287m, 105.8524m, 3m), new("Cầu Giấy", 21.03m, 105.79m, 3m)];

    /// <summary>A trip searching a zone of Hoàn Kiếm and Cầu Giấy, as locate_places stores it.</summary>
    private static AgentState ZoneState() => AgentTestData.State() with
    {
        Province = "Hà Nội", OriginMode = "pinned", OriginLatitude = 21.0287m, OriginLongitude = 105.8524m, OriginLabel = "Nội Đô Hà Nội", RadiusKm = 3m,
        NamedAreaLabel = "Nội Đô Hà Nội", OriginZone = Zone
    };

    // TEST-01: A day in a zone, or a trip in one, has no start point, so its meals are not pulled to the first place; one place and GPS keep theirs.
    /// <summary>"Nội đô" listed as [Hoàn Kiếm, Ba Đình, …] used to gather every meal around Hoàn Kiếm.</summary>
    [Fact]
    public void StartPoint_Zone_IsNone()
    {
        var zoneDay = new AgentState { DayAreas = [new AgentDayArea(1, "Trung Tâm", 21.0287m, 105.8524m, 3m, null, Zone), new AgentDayArea(2, "Cầu Giấy", 21.03m, 105.79m, 3m)] };

        AgentScopedPlanning.StartPoint(zoneDay, 1).Should().BeNull();
        AgentScopedPlanning.StartPoint(zoneDay, 2).Should().Be((21.03, 105.79));
        AgentScopedPlanning.StartPoint(ZoneState(), 1).Should().BeNull();
        AgentScopedPlanning.StartPoint(new AgentState { OriginMode = "gps", OriginLatitude = 21m, OriginLongitude = 105m }, 1).Should().Be((21.0, 105.0));
    }

    // TEST-02: A radius the user narrows applies to every place of the zone, which stays a zone; a later point on the map replaces it.
    /// <summary>Before, any radius other than the first place's own dropped the zone to one circle.</summary>
    [Fact]
    public void Area_ZoneWithNarrowedRadiusOrNewPin()
    {
        var narrowed = AgentScopedPlanning.Area(ZoneState() with { RadiusKm = 2m }, 1)!;
        var moved = ZoneState() with { OriginLatitude = 21.05m, OriginLongitude = 105.80m };

        narrowed.Circles().Select(circle => circle.RadiusKm).Should().Equal(2m, 2m);
        AgentScopedPlanning.HasZoneOrigin(ZoneState()).Should().BeTrue();
        AgentScopedPlanning.HasZoneOrigin(moved).Should().BeFalse("a point picked later replaces the zone");
        AgentScopedPlanning.Area(moved, 1)!.Circles().Should().ContainSingle();
    }

    // TEST-03: A roadmap saved from a zone has no pin, while a real pinned point and GPS save as before.
    /// <summary>The roadmap page used to show "Nội Đô Hà Nội" pinned at one ward.</summary>
    [Fact]
    public void RoadmapOrigin_Zone_SavesNoPin()
    {
        AgentProposalCommitter.RoadmapOrigin(ZoneState()).Should().Be((RoadmapOriginMode.None, (decimal?)null, (decimal?)null, (string?)null));
        AgentProposalCommitter.RoadmapOrigin(ZoneState() with { OriginZone = null })
            .Should().Be((RoadmapOriginMode.Pinned, (decimal?)21.0287m, (decimal?)105.8524m, (string?)"Nội Đô Hà Nội"));
        AgentProposalCommitter.RoadmapOrigin(new AgentState { OriginMode = "gps", OriginLatitude = 21m, OriginLongitude = 105m }).Mode.Should().Be(RoadmapOriginMode.Gps);
    }

    // TEST-04: A route of a zone trip starts at its first dish, not at the first place of the zone.
    /// <summary>The route estimate would otherwise add a leg from Hoàn Kiếm to a dish in Cầu Giấy.</summary>
    [Fact]
    public async Task EstimateAsync_Zone_HasNoOrigin()
    {
        var fixture = AgentTestData.Validator();
        fixture.Routes.Setup(x => x.CalculateRouteAsync(null, It.IsAny<IReadOnlyList<RouteCoordinate>>(), RouteProfile.Driving, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouteCalculationResult([], true));

        var result = await new AgentRouteEstimator(fixture.Routes.Object).EstimateAsync(ZoneState(), 1,
            [AgentTestData.Item() with { Latitude = 21.03m, Longitude = 105.79m }], RouteProfile.Driving, CancellationToken.None);

        result.OriginSkipped.Should().BeFalse();
        fixture.Routes.Verify(x => x.CalculateRouteAsync(null, It.IsAny<IReadOnlyList<RouteCoordinate>>(), RouteProfile.Driving, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-05: A model copying the zone's point back rounded gets the backend's point, so the zone stays; the radius it narrows is kept.
    /// <summary>Without this, 21.0287 written as 21.03 silently dropped the zone to one circle.</summary>
    [Fact]
    public void Accept_ZonePointRounded_KeepsBackendPoint()
    {
        var accepted = new AgentConstraintGuard().Accept(ZoneState(), ZoneState() with { OriginLatitude = 21.03m, OriginLongitude = 105.85m, OriginLabel = "nội đô", RadiusKm = 2m });

        accepted.OriginLatitude.Should().Be(21.0287m);
        accepted.OriginLongitude.Should().Be(105.8524m);
        accepted.OriginLabel.Should().Be("Nội Đô Hà Nội");
        accepted.RadiusKm.Should().Be(2m);
        AgentScopedPlanning.HasZoneOrigin(accepted).Should().BeTrue();
    }

    // TEST-06: A roadmap edited outside the chat, saved with no origin because it cannot hold a zone, keeps the conversation's zone; a pin set on the roadmap replaces it.
    /// <summary>After a zone plan was saved, any edit on the roadmap page used to wipe the zone on the next message.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BuildAsync_RoadmapEditedOutside_KeepsZoneUnlessPinned(bool roadmapPinned, bool zoneKept)
    {
        // STEP-01: A roadmap changed after the state's snapshot, with no origin or with a pin of its own.
        var roadmaps = new Mock<IRoadmapRepository>();
        roadmaps.Setup(repo => repo.GetOwnedDetailAsync(88, 3, It.IsAny<CancellationToken>())).ReturnsAsync(new ANGI.Domain.Entities.Roadmap
        {
            Id = 88, UpdatedAt = DateTime.UnixEpoch.AddSeconds(9), Title = "Official", NumDays = 2, DestinationProvince = "Hà Nội",
            OriginMode = roadmapPinned ? RoadmapOriginMode.Pinned : RoadmapOriginMode.None,
            OriginLatitude = roadmapPinned ? 21.05m : null, OriginLongitude = roadmapPinned ? 105.80m : null, OriginLabel = roadmapPinned ? "Nhà" : null
        });
        var repository = new Mock<IAgentRepository>();
        repository.Setup(repo => repo.GetCompletedHistoryAsync(7, 20, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var conversation = new AgentConversation { Id = 7, UserId = 3, RoadmapId = 88, State = AgentJson.Write(ZoneState() with { SourceRoadmapUpdatedAt = DateTime.UnixEpoch.AddSeconds(4) }) };

        // STEP-02: Build the context of the next message.
        var result = await new AgentContextBuilder(repository.Object, roadmaps.Object, new(), TimeProvider.System, AgentTestData.Validator().Tags.Object)
            .BuildAsync(conversation, new(), CancellationToken.None);

        // STEP-03: Assert the zone is kept only when the roadmap has no pin of its own.
        AgentScopedPlanning.HasZoneOrigin(result.State).Should().Be(zoneKept);
        result.State.OriginLatitude.Should().Be(roadmapPinned ? 21.05m : 21.0287m);
    }
}
