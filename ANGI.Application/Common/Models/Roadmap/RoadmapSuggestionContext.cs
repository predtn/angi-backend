using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Roadmap;

/// <summary>Minimal owned-roadmap data needed to filter RM-06 candidates.</summary>
public sealed record RoadmapSuggestionContext(
    RoadmapOriginMode OriginMode,
    decimal? OriginLatitude,
    decimal? OriginLongitude,
    IReadOnlyList<int> ExistingDishIds);
