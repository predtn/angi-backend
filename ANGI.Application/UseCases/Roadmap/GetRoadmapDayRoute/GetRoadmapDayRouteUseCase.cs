using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Roadmap.GetRoadmapDayRoute;

/// <summary>Implements RM-14 origin selection, route calculation, and response leg mapping.</summary>
public sealed class GetRoadmapDayRouteUseCase : IGetRoadmapDayRouteUseCase
{
    private const double EarthRadiusMeters = 6_371_000d;
    private const double MaximumOriginDistanceMeters = 30_000d;

    private readonly IValidator<GetRoadmapDayRouteRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IRouteService _routeService;

    /// <summary>Initializes validation, persistence, identity, and route-provider dependencies.</summary>
    public GetRoadmapDayRouteUseCase(
        IValidator<GetRoadmapDayRouteRequestDto> validator,
        IRoadmapRepository roadmapRepository,
        ICurrentUserService currentUserService,
        IRouteService routeService)
    {
        _validator = validator;
        _roadmapRepository = roadmapRepository;
        _currentUserService = currentUserService;
        _routeService = routeService;
    }

    /// <summary>Validates RM-14, selects its effective origin, and returns ordered daily travel metrics.</summary>
    public async Task<GetRoadmapDayRouteResponseDto> ExecuteAsync(
        long roadmapId,
        long dayId,
        GetRoadmapDayRouteRequestDto request,
        CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare userId, roadmap from roadmapRepository.GetOwnedRouteDataAsync, day, items through OrderItems and 3 more.
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem lộ trình.");
        var roadmap = await _roadmapRepository.GetOwnedRouteDataAsync(roadmapId, dayId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
        var day = roadmap.Day
            ?? throw new NotFoundException("DAY_NOT_FOUND", "Không tìm thấy ngày trong roadmap.");
        var items = OrderItems(day.Items);
        var destinations = items
            .Select(item => new RouteCoordinate(item.Latitude, item.Longitude))
            .ToArray();
        var origin = SelectOrigin(roadmap, request);
        var originSkipped = origin is not null && destinations.Length > 0 &&
                            HaversineMeters(origin.Coordinate, destinations[0]) > MaximumOriginDistanceMeters;
        // STEP-03: When originSkipped is true, set origin.
        if (originSkipped)
        {
            origin = null;
        }

        // STEP-04: Prepare profile, then call routeService.CalculateRouteAsync and keep route.
        var profile = ParseProfile(request.Profile!);
        var route = await _routeService.CalculateRouteAsync(
            origin?.Coordinate,
            destinations,
            profile,
            ct);
        // STEP-05: Prepare legs through MapLegs.
        var legs = MapLegs(items, route.Legs, origin is not null);

        // STEP-06: Return a new GetRoadmapDayRouteResponseDto.
        return new GetRoadmapDayRouteResponseDto
        {
            DayId = day.Id,
            Profile = ToApiValue(profile),
            Origin = origin is null
                ? null
                : new RoadmapRouteOriginDto
                {
                    Latitude = origin.Coordinate.Latitude,
                    Longitude = origin.Coordinate.Longitude,
                    Label = origin.Label,
                    Source = origin.Source
                },
            OriginSkipped = originSkipped,
            Legs = legs,
            TotalDistanceMeters = checked(legs.Sum(leg => leg.DistanceMeters)),
            TotalDurationSeconds = checked(legs.Sum(leg => leg.DurationSeconds)),
            IsEstimated = route.IsEstimated
        };
    }

    /// <summary>Orders destinations by meal, explicit sort order, and id for deterministic ties.</summary>
    private static RoadmapRouteItemData[] OrderItems(IEnumerable<RoadmapRouteItemData> items) => items
        .OrderBy(item => item.MealSlot)
        .ThenBy(item => item.SortOrder)
        .ThenBy(item => item.Id)
        .ToArray();

    /// <summary>Selects request GPS first, otherwise a persisted pinned origin, otherwise no origin.</summary>
    private static SelectedOrigin? SelectOrigin(
        RoadmapRouteData roadmap,
        GetRoadmapDayRouteRequestDto request)
    {
        // STEP-01: Return a new SelectedOrigin early when request.OriginLat has a value and request.OriginLng has a value.
        if (request.OriginLat.HasValue && request.OriginLng.HasValue)
        {
            return new SelectedOrigin(
                new RouteCoordinate(request.OriginLat.Value, request.OriginLng.Value),
                null,
                "gps");
        }

        // STEP-02: Return a new SelectedOrigin early when roadmap.OriginMode is RoadmapOriginMode.Pinned, roadmap.OriginLatitude has a value and roadmap.OriginLongitude has a value.
        if (roadmap.OriginMode == RoadmapOriginMode.Pinned &&
            roadmap.OriginLatitude.HasValue &&
            roadmap.OriginLongitude.HasValue)
        {
            return new SelectedOrigin(
                new RouteCoordinate(roadmap.OriginLatitude.Value, roadmap.OriginLongitude.Value),
                roadmap.OriginLabel,
                "pinned");
        }

        // STEP-03: Return null.
        return null;
    }

    /// <summary>Maps provider metrics to item identifiers for origin and non-origin point sequences.</summary>
    private static IReadOnlyList<RoadmapRouteLegDto> MapLegs(
        IReadOnlyList<RoadmapRouteItemData> items,
        IReadOnlyList<RouteMetric> metrics,
        bool hasOrigin)
    {
        // STEP-01: Prepare expectedCount through Math.Max.
        var expectedCount = Math.Max(0, items.Count - (hasOrigin ? 0 : 1));
        // STEP-02: Throw InvalidOperationException when metrics.Count is not expectedCount.
        if (metrics.Count != expectedCount)
        {
            throw new InvalidOperationException("The route provider returned an unexpected number of legs.");
        }

        // STEP-03: Return the result of metrics.Select(filtered).ToArray.
        return metrics.Select((metric, index) => new RoadmapRouteLegDto
        {
            FromItemId = hasOrigin && index == 0 ? null : items[hasOrigin ? index - 1 : index].Id,
            ToItemId = items[hasOrigin ? index : index + 1].Id,
            DistanceMeters = metric.DistanceMeters,
            DurationSeconds = metric.DurationSeconds
        }).ToArray();
    }

    /// <summary>Parses the validated public route profile into its provider-independent enum.</summary>
    private static RouteProfile ParseProfile(string value) => value.Trim().ToLowerInvariant() switch
    {
        "walking" => RouteProfile.Walking,
        "cycling" => RouteProfile.Cycling,
        _ => RouteProfile.Driving
    };

    /// <summary>Converts a route profile to the API's lowercase enum value.</summary>
    private static string ToApiValue(RouteProfile profile) => profile.ToString().ToLowerInvariant();

    /// <summary>Calculates straight-line distance between two WGS84 coordinates for the 30 km origin rule.</summary>
    private static double HaversineMeters(RouteCoordinate from, RouteCoordinate to)
    {
        // STEP-01: Prepare latitude1 through DegreesToRadians, latitude2 through DegreesToRadians, latitudeDelta, longitudeDelta through DegreesToRadians and 1 more.
        var latitude1 = DegreesToRadians((double)from.Latitude);
        var latitude2 = DegreesToRadians((double)to.Latitude);
        var latitudeDelta = latitude2 - latitude1;
        var longitudeDelta = DegreesToRadians((double)(to.Longitude - from.Longitude));
        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2d), 2d) +
                        Math.Cos(latitude1) * Math.Cos(latitude2) *
                        Math.Pow(Math.Sin(longitudeDelta / 2d), 2d);
        // STEP-02: Return the computed condition.
        return EarthRadiusMeters * 2d * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1d - haversine));
    }

    /// <summary>Converts angular degrees to radians for Haversine calculations.</summary>
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private sealed record SelectedOrigin(RouteCoordinate Coordinate, string? Label, string Source);
}
