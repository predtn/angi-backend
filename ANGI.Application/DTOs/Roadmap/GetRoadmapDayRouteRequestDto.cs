namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Contains the optional GPS origin and travel profile accepted by RM-14.</summary>
public sealed class GetRoadmapDayRouteRequestDto
{
    public decimal? OriginLat { get; set; }
    public decimal? OriginLng { get; set; }
    public string? Profile { get; set; } = "driving";
}
