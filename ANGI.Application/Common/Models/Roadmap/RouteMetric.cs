namespace ANGI.Application.Common.Models.Roadmap
{
    /// <summary>Distance and duration returned for one pair of consecutive route coordinates.</summary>
    public sealed record RouteMetric(int DistanceMeters, int DurationSeconds);
}
