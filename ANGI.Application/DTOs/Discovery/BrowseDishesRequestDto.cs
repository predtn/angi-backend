namespace ANGI.Application.DTOs.Discovery;

/// <summary>Query string of DISH-01. With a cursor, the filters are ignored: the cursor names an already ranked list.</summary>
public sealed class BrowseDishesRequestDto
{
    /// <summary>Case-insensitive substring of the dish name or the restaurant name.</summary>
    public string? Q { get; init; }

    public string? Province { get; init; }

    /// <summary>OR within one tag type, AND across tag types.</summary>
    public short[]? TagIds { get; init; }

    /// <summary>Dishes with any of these tags are left out.</summary>
    public short[]? ExcludeTagIds { get; init; }

    /// <summary>soup | dry | other.</summary>
    public string? DishForm { get; init; }

    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public long? RestaurantId { get; init; }
    public decimal? Lat { get; init; }
    public decimal? Lng { get; init; }

    /// <summary>≤ 50, needs lat and lng; 5 km when lat and lng come without it.</summary>
    public decimal? RadiusKm { get; init; }

    public string? Cursor { get; init; }

    /// <summary>1..50, 20 by default.</summary>
    public int? Limit { get; init; }
}
