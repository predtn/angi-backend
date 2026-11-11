namespace ANGI.Application.Common.Models.Roadmap
{
    /// <summary>Provider-independent leg metrics returned by the shared route service.</summary>
    public sealed record RouteCalculationResult(
        IReadOnlyList<RouteMetric> Legs,
        bool IsEstimated);
}
