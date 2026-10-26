using ANGI.Application.Common.Models.Roadmap;

namespace ANGI.Application.Common.Models.Agent;

/// <summary>Computed travel metrics for one preview day; never stored in official roadmap rows.</summary>
public sealed record AgentDayRoute(short DayNumber, bool IsEstimated, bool OriginSkipped,
    IReadOnlyList<RouteMetric> Legs)
{
    /// <summary>Returns the route's total metres without overflowing a 32-bit leg accumulator.</summary>
    public long TotalDistanceMeters => Legs.Sum(leg => (long)leg.DistanceMeters);
    /// <summary>Returns the route's total estimated or provider-supplied travel seconds.</summary>
    public long TotalDurationSeconds => Legs.Sum(leg => (long)leg.DurationSeconds);
}
