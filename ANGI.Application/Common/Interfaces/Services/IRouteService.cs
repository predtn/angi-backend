using ANGI.Application.Common.Models.Roadmap;

namespace ANGI.Application.Common.Interfaces.Services
{
    /// <summary>Calculates travel metrics for ordered roadmap coordinates through an external directions provider.</summary>
    public interface IRouteService
    {
        /// <summary>
        /// Calculates ordered legs from an optional origin through the supplied destinations. The caller decides
        /// whether an origin should be used; provider failures return estimated metrics for the same point sequence.
        /// </summary>
        Task<RouteCalculationResult> CalculateRouteAsync(
            RouteCoordinate? origin,
            IReadOnlyList<RouteCoordinate> destinations,
            RouteProfile profile,
            CancellationToken ct);
    }
}
