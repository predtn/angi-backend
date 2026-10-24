using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.GetRoadmapDayRoute;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.GetRoadmapDayRoute;

public sealed class GetRoadmapDayRouteUseCaseTests
{
    // TEST-01: Prefer request GPS over a pinned origin and map origin-to-item plus item-to-item legs.
    /// <summary>Verifies GPS precedence, destination ordering, identifiers, totals, and provider delegation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithGpsOrigin_ShouldReturnOrderedMappedRoute()
    {
        var roadmap = CreateRoadmapWithDay();
        roadmap.OriginMode = RoadmapOriginMode.Pinned;
        roadmap.OriginLatitude = 16.05m;
        roadmap.OriginLongitude = 108.20m;
        roadmap.OriginLabel = "Hotel";
        AddItem(roadmap, CreateItem(503, MealSlot.Dinner, 0, 16.03m, 108.03m));
        AddItem(roadmap, CreateItem(501, MealSlot.Breakfast, 2, 16.01m, 108.01m));
        AddItem(roadmap, CreateItem(502, MealSlot.Breakfast, 1, 16.02m, 108.02m));
        var routeService = new Mock<IRouteService>();
        routeService.Setup(item => item.CalculateRouteAsync(
                It.IsAny<RouteCoordinate?>(),
                It.IsAny<IReadOnlyList<RouteCoordinate>>(),
                RouteProfile.Cycling,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouteCalculationResult(
                [new RouteMetric(10, 1), new RouteMetric(20, 2), new RouteMetric(30, 3)], false));
        var useCase = CreateUseCase(roadmap, routeService.Object);

        var result = await useCase.ExecuteAsync(88, 301, new GetRoadmapDayRouteRequestDto
        {
            OriginLat = 16.000m,
            OriginLng = 108.000m,
            Profile = "cycling"
        }, CancellationToken.None);

        result.Profile.Should().Be("cycling");
        result.Origin.Should().BeEquivalentTo(new
        {
            Latitude = 16.000m,
            Longitude = 108.000m,
            Label = (string?)null,
            Source = "gps"
        });
        result.OriginSkipped.Should().BeFalse();
        result.Legs.Select(leg => (leg.FromItemId, leg.ToItemId)).Should().Equal(
            ((long?)null, 502L), ((long?)502, 501L), ((long?)501, 503L));
        result.TotalDistanceMeters.Should().Be(60);
        result.TotalDurationSeconds.Should().Be(6);
        result.IsEstimated.Should().BeFalse();
        routeService.Verify(item => item.CalculateRouteAsync(
            new RouteCoordinate(16.000m, 108.000m),
            It.Is<IReadOnlyList<RouteCoordinate>>(points => points.SequenceEqual(new[]
            {
                new RouteCoordinate(16.02m, 108.02m),
                new RouteCoordinate(16.01m, 108.01m),
                new RouteCoordinate(16.03m, 108.03m)
            })),
            RouteProfile.Cycling,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Use a persisted pinned origin when the request has no GPS coordinates.
    /// <summary>Verifies pinned coordinate and label mapping for RM-14.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutGpsAndWithPinnedRoadmap_ShouldUsePinnedOrigin()
    {
        var roadmap = CreateRoadmapWithDay();
        roadmap.OriginMode = RoadmapOriginMode.Pinned;
        roadmap.OriginLatitude = 16m;
        roadmap.OriginLongitude = 108m;
        roadmap.OriginLabel = "Hotel";
        AddItem(roadmap, CreateItem(501, MealSlot.Lunch, 0, 16.01m, 108.01m));
        var routeService = RouteReturning(new RouteCalculationResult([new RouteMetric(100, 20)], false));
        var useCase = CreateUseCase(roadmap, routeService.Object);

        var result = await useCase.ExecuteAsync(88, 301, new GetRoadmapDayRouteRequestDto(), CancellationToken.None);

        result.Origin.Should().BeEquivalentTo(new
        {
            Latitude = 16m,
            Longitude = 108m,
            Label = "Hotel",
            Source = "pinned"
        });
        result.Legs.Should().ContainSingle().Which.FromItemId.Should().BeNull();
    }

    // TEST-03: Route directly between items for none and gps roadmaps without request GPS.
    /// <summary>Verifies both persisted modes that must not manufacture an origin.</summary>
    [Theory]
    [InlineData(RoadmapOriginMode.None)]
    [InlineData(RoadmapOriginMode.Gps)]
    public async Task ExecuteAsync_WithoutEffectiveOrigin_ShouldStartAtFirstItem(RoadmapOriginMode mode)
    {
        var roadmap = CreateRoadmapWithDay();
        roadmap.OriginMode = mode;
        AddItem(roadmap, CreateItem(501, MealSlot.Breakfast, 0, 16m, 108m));
        AddItem(roadmap, CreateItem(502, MealSlot.Lunch, 0, 16.01m, 108.01m));
        var routeService = RouteReturning(new RouteCalculationResult([new RouteMetric(100, 20)], false));
        var useCase = CreateUseCase(roadmap, routeService.Object);

        var result = await useCase.ExecuteAsync(88, 301, new GetRoadmapDayRouteRequestDto(), CancellationToken.None);

        result.Origin.Should().BeNull();
        result.OriginSkipped.Should().BeFalse();
        result.Legs.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            FromItemId = (long?)501,
            ToItemId = 502L,
            DistanceMeters = 100,
            DurationSeconds = 20
        });
        routeService.Verify(item => item.CalculateRouteAsync(
            null,
            It.IsAny<IReadOnlyList<RouteCoordinate>>(),
            RouteProfile.Driving,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-04: Keep origins below 30 km and skip origins above 30 km.
    /// <summary>Verifies both sides of the RM-14 straight-line origin-distance threshold.</summary>
    [Theory]
    [InlineData(0.269, false)]
    [InlineData(0.270, true)]
    public async Task ExecuteAsync_AroundThirtyKilometerBoundary_ShouldApplyOriginSkipRule(
        decimal destinationLatitude,
        bool expectedSkipped)
    {
        var roadmap = CreateRoadmapWithDay();
        AddItem(roadmap, CreateItem(501, MealSlot.Breakfast, 0, destinationLatitude, 0m));
        var routeService = new Mock<IRouteService>();
        routeService.Setup(item => item.CalculateRouteAsync(
                It.IsAny<RouteCoordinate?>(),
                It.IsAny<IReadOnlyList<RouteCoordinate>>(),
                It.IsAny<RouteProfile>(),
                It.IsAny<CancellationToken>()))
            .Returns((RouteCoordinate? origin, IReadOnlyList<RouteCoordinate> destinations, RouteProfile _, CancellationToken _) =>
                Task.FromResult(new RouteCalculationResult(
                    origin is null ? Array.Empty<RouteMetric>() : [new RouteMetric(1, 1)],
                    false)));
        var useCase = CreateUseCase(roadmap, routeService.Object);

        var result = await useCase.ExecuteAsync(88, 301, new GetRoadmapDayRouteRequestDto
        {
            OriginLat = 0m,
            OriginLng = 0m
        }, CancellationToken.None);

        result.OriginSkipped.Should().Be(expectedSkipped);
        if (expectedSkipped)
        {
            result.Origin.Should().BeNull();
        }
        else
        {
            result.Origin.Should().NotBeNull();
        }
        routeService.Verify(item => item.CalculateRouteAsync(
            expectedSkipped ? null : new RouteCoordinate(0m, 0m),
            It.IsAny<IReadOnlyList<RouteCoordinate>>(),
            RouteProfile.Driving,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-05: Return empty totals for zero items and one item without an origin.
    /// <summary>Verifies both lower point-count boundaries passed through to the shared route service.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ExecuteAsync_WithFewerThanTwoEffectivePoints_ShouldReturnEmptyRoute(int itemCount)
    {
        var roadmap = CreateRoadmapWithDay();
        if (itemCount == 1)
        {
            AddItem(roadmap, CreateItem(501, MealSlot.Breakfast, 0, 16m, 108m));
        }

        var routeService = RouteReturning(new RouteCalculationResult(Array.Empty<RouteMetric>(), false));
        var useCase = CreateUseCase(roadmap, routeService.Object);

        var result = await useCase.ExecuteAsync(88, 301, new GetRoadmapDayRouteRequestDto(), CancellationToken.None);

        result.Legs.Should().BeEmpty();
        result.TotalDistanceMeters.Should().Be(0);
        result.TotalDurationSeconds.Should().Be(0);
    }

    // TEST-06: Preserve the provider fallback marker and each supported normalized profile.
    /// <summary>Verifies profile parsing and estimated-route propagation for all API enum values.</summary>
    [Theory]
    [InlineData("DRIVING", RouteProfile.Driving, "driving")]
    [InlineData("walking", RouteProfile.Walking, "walking")]
    [InlineData("cycling", RouteProfile.Cycling, "cycling")]
    public async Task ExecuteAsync_WithSupportedProfile_ShouldNormalizeAndPropagateEstimate(
        string requested,
        RouteProfile expectedProfile,
        string expectedApiValue)
    {
        var roadmap = CreateRoadmapWithDay();
        var routeService = RouteReturning(new RouteCalculationResult(Array.Empty<RouteMetric>(), true));
        var useCase = CreateUseCase(roadmap, routeService.Object);

        var result = await useCase.ExecuteAsync(88, 301,
            new GetRoadmapDayRouteRequestDto { Profile = requested }, CancellationToken.None);

        result.Profile.Should().Be(expectedApiValue);
        result.IsEstimated.Should().BeTrue();
        routeService.Verify(item => item.CalculateRouteAsync(
            It.IsAny<RouteCoordinate?>(),
            It.IsAny<IReadOnlyList<RouteCoordinate>>(),
            expectedProfile,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-07: Return ROADMAP_NOT_FOUND before checking a day when ownership lookup fails.
    /// <summary>Verifies missing and foreign roadmaps share the non-disclosing RM-14 error.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenOwnedRoadmapDoesNotExist_ShouldThrowRoadmapNotFound()
    {
        var useCase = CreateUseCase(null, Mock.Of<IRouteService>());

        var act = () => useCase.ExecuteAsync(404, 301, new GetRoadmapDayRouteRequestDto(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
    }

    // TEST-08: Return DAY_NOT_FOUND only when the owned roadmap exists without the requested day.
    /// <summary>Verifies the documented distinction between roadmap and day absence.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDayDoesNotExist_ShouldThrowDayNotFound()
    {
        var roadmap = CreateRoadmapWithDay();
        roadmap.Day = null;
        var useCase = CreateUseCase(roadmap, Mock.Of<IRouteService>());

        var act = () => useCase.ExecuteAsync(88, 999, new GetRoadmapDayRouteRequestDto(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("DAY_NOT_FOUND");
    }

    // TEST-09: Validate query input before identity, repository, or route-provider work.
    /// <summary>Verifies incomplete GPS coordinates stop RM-14 at its first required operation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidQuery_ShouldThrowValidationBeforeRepositoryLookup()
    {
        var repository = new Mock<IRoadmapRepository>();
        var routeService = new Mock<IRouteService>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        var useCase = new GetRoadmapDayRouteUseCase(
            new GetRoadmapDayRouteRequestDtoValidator(), repository.Object, currentUser.Object, routeService.Object);

        var act = () => useCase.ExecuteAsync(88, 301,
            new GetRoadmapDayRouteRequestDto { OriginLat = 10m }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        repository.VerifyNoOtherCalls();
        routeService.VerifyNoOtherCalls();
    }

    // TEST-10: Reject execution without an authenticated user before persistence access.
    /// <summary>Verifies RM-14 remains protected when invoked outside the controller pipeline.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        var repository = new Mock<IRoadmapRepository>();
        var currentUser = new Mock<ICurrentUserService>();
        var useCase = new GetRoadmapDayRouteUseCase(
            new GetRoadmapDayRouteRequestDtoValidator(), repository.Object, currentUser.Object, Mock.Of<IRouteService>());

        var act = () => useCase.ExecuteAsync(88, 301, new GetRoadmapDayRouteRequestDto(), CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        repository.VerifyNoOtherCalls();
    }

    /// <summary>Creates an owned roadmap containing the requested route day.</summary>
    private static RoadmapRouteData CreateRoadmapWithDay()
    {
        return new RoadmapRouteData
        {
            OriginMode = RoadmapOriginMode.None,
            Day = new RoadmapDayRouteData
            {
                Id = 301,
                Items = new List<RoadmapRouteItemData>()
            }
        };
    }

    /// <summary>Adds one item to the mutable list used by a unit-test route projection.</summary>
    private static void AddItem(RoadmapRouteData roadmap, RoadmapRouteItemData item) =>
        ((List<RoadmapRouteItemData>)roadmap.Day!.Items).Add(item);

    /// <summary>Creates one projected route item with its restaurant coordinate.</summary>
    private static RoadmapRouteItemData CreateItem(
        long id,
        MealSlot mealSlot,
        short sortOrder,
        decimal latitude,
        decimal longitude)
    {
        return new RoadmapRouteItemData
        {
            Id = id,
            MealSlot = mealSlot,
            SortOrder = sortOrder,
            Latitude = latitude,
            Longitude = longitude
        };
    }

    /// <summary>Creates a route-service mock returning a deterministic provider result.</summary>
    private static Mock<IRouteService> RouteReturning(RouteCalculationResult result)
    {
        var routeService = new Mock<IRouteService>();
        routeService.Setup(item => item.CalculateRouteAsync(
                It.IsAny<RouteCoordinate?>(),
                It.IsAny<IReadOnlyList<RouteCoordinate>>(),
                It.IsAny<RouteProfile>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return routeService;
    }

    /// <summary>Creates RM-14 with deterministic ownership and repository results.</summary>
    private static GetRoadmapDayRouteUseCase CreateUseCase(
        RoadmapRouteData? roadmap,
        IRouteService routeService)
    {
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(item => item.GetOwnedRouteDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(roadmap);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        return new GetRoadmapDayRouteUseCase(
            new GetRoadmapDayRouteRequestDtoValidator(), repository.Object, currentUser.Object, routeService);
    }
}
