namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Query string of RM-06; filters are ignored after a ranked-list cursor has been issued.</summary>
public sealed class GetRoadmapSuggestionsRequestDto
{
    /// <summary>Destination province supplied by the app because a roadmap does not persist it.</summary>
    public string Province { get; init; } = null!;

    public long? DayId { get; init; }

    /// <summary>breakfast | lunch | dinner | snack | late_night.</summary>
    public string? MealSlot { get; init; }

    public string? Q { get; init; }
    public short[]? TagIds { get; init; }
    public short[]? ExcludeTagIds { get; init; }
    public decimal? MaxPrice { get; init; }
    public decimal? Lat { get; init; }
    public decimal? Lng { get; init; }

    /// <summary>At most 50 km; defaults to 10 km when GPS or a pinned roadmap origin is available.</summary>
    public decimal? RadiusKm { get; init; }

    public string? Cursor { get; init; }

    /// <summary>1..50, 20 by default.</summary>
    public int? Limit { get; init; }
}
