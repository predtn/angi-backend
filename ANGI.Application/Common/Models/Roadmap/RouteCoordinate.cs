namespace ANGI.Application.Common.Models.Roadmap
{
    /// <summary>One WGS84 coordinate supplied to the route service.</summary>
    public sealed record RouteCoordinate(decimal Latitude, decimal Longitude);
}
