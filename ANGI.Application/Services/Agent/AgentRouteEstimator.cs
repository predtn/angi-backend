using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Roadmap;

namespace ANGI.Application.Services.Agent;

/// <summary>Reuses RM-14 routing for unsaved proposal coordinates without invoking an HTTP controller.</summary>
public sealed class AgentRouteEstimator(IRouteService routes)
{
    /// <summary>Bounds route points and reports estimates and skipped origins without inventing missing coordinates.</summary>
    public async Task<AgentDayRoute> EstimateAsync(AgentState state, short dayNumber, AgentItem[] items,
        RouteProfile profile, CancellationToken ct)
    {
        if (items.Length > 24 || items.Any(x => !x.Latitude.HasValue || !x.Longitude.HasValue ||
            x.Latitude < -90 || x.Latitude > 90 || x.Longitude < -180 || x.Longitude > 180))
            throw new ConflictException("PROPOSAL_NOT_COMMITTABLE", "Tuyến thiếu tọa độ hợp lệ hoặc vượt giới hạn điểm.");
        RouteCoordinate? origin = state.OriginLatitude is { } lat && state.OriginLongitude is { } lng
            ? new(lat, lng) : null;
        var skipped = origin is not null && items.Length > 0 && NearbyArea.DistanceKm(origin.Latitude, origin.Longitude,
            items[0].Latitude!.Value, items[0].Longitude!.Value) > 30;
        if (skipped) origin = null;
        var destinations = items.Select(x => new RouteCoordinate(x.Latitude!.Value, x.Longitude!.Value)).ToArray();
        var result = await routes.CalculateRouteAsync(origin, destinations, profile, ct);
        var expectedLegs = Math.Max(0, destinations.Length + (origin is null ? 0 : 1) - 1);
        if (result.Legs.Count != expectedLegs || result.Legs.Any(x => x.DistanceMeters < 0 || x.DurationSeconds < 0))
            throw new ServiceUnavailableException("AGENT_UNAVAILABLE", "Không kiểm tra được kết quả tuyến đường.");
        return new(dayNumber, result.IsEstimated, skipped, result.Legs);
    }
}
