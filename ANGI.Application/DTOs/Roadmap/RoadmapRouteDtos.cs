namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Represents the effective origin used to calculate an RM-14 route.</summary>
public sealed class RoadmapRouteOriginDto
{
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Label { get; set; }
    public string Source { get; set; } = string.Empty;
}

/// <summary>Represents travel metrics between two consecutive roadmap points.</summary>
public sealed class RoadmapRouteLegDto
{
    public long? FromItemId { get; set; }
    public long ToItemId { get; set; }
    public int DistanceMeters { get; set; }
    public int DurationSeconds { get; set; }
}

/// <summary>Represents the ordered travel route and totals for one roadmap day.</summary>
public sealed class GetRoadmapDayRouteResponseDto
{
    public long DayId { get; set; }
    public string Profile { get; set; } = string.Empty;
    public RoadmapRouteOriginDto? Origin { get; set; }
    public bool OriginSkipped { get; set; }
    public IReadOnlyList<RoadmapRouteLegDto> Legs { get; set; } = Array.Empty<RoadmapRouteLegDto>();
    public int TotalDistanceMeters { get; set; }
    public int TotalDurationSeconds { get; set; }
    public bool IsEstimated { get; set; }
}
