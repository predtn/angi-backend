using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Roadmap;

/// <summary>Contains only the persisted roadmap values required to calculate one RM-14 route.</summary>
public sealed class RoadmapRouteData
{
    public RoadmapOriginMode OriginMode { get; set; }
    public decimal? OriginLatitude { get; set; }
    public decimal? OriginLongitude { get; set; }
    public string? OriginLabel { get; set; }
    public RoadmapDayRouteData? Day { get; set; }
}

/// <summary>Contains one requested roadmap day and its route stops.</summary>
public sealed class RoadmapDayRouteData
{
    public long Id { get; set; }
    public IReadOnlyList<RoadmapRouteItemData> Items { get; set; } = Array.Empty<RoadmapRouteItemData>();
}

/// <summary>Contains one ordered item identifier and restaurant coordinate used by RM-14.</summary>
public sealed class RoadmapRouteItemData
{
    public long Id { get; set; }
    public MealSlot MealSlot { get; set; }
    public short SortOrder { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
}
