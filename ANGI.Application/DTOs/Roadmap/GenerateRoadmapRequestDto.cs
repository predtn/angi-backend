namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Request body of RM-10.</summary>
public sealed class GenerateRoadmapRequestDto
{
    /// <summary>full | day | slot.</summary>
    public string Scope { get; init; } = null!;

    /// <summary>Required when scope is day or slot.</summary>
    public long? DayId { get; init; }

    /// <summary>Required when scope is slot: breakfast | lunch | dinner | snack | late_night.</summary>
    public string? MealSlot { get; init; }

    /// <summary>true removes the items of the scope first; false (default) only fills empty meals.</summary>
    public bool? ReplaceExisting { get; init; }

    /// <summary>Province to take dishes from (the roadmap stores no destination).</summary>
    public string Province { get; init; } = null!;

    /// <summary>Meals to generate for scope full / day; breakfast, lunch, dinner by default.</summary>
    public string[]? MealSlots { get; init; }

    /// <summary>Highest price of one dish.</summary>
    public decimal? MaxPricePerMeal { get; init; }

    /// <summary>Dishes with these tags are preferred.</summary>
    public short[]? TagIds { get; init; }

    /// <summary>Dishes with any of these tags are left out.</summary>
    public short[]? ExcludeTagIds { get; init; }

    /// <summary>Note for this generation (≤ 500), kept in the job.</summary>
    public string? Note { get; init; }

    /// <summary>Current GPS position, used when the roadmap's originMode is gps.</summary>
    public decimal? CurrentLatitude { get; init; }

    public decimal? CurrentLongitude { get; init; }

    /// <summary>1..50 km around the origin; 10 by default; ignored without an origin.</summary>
    public decimal? RadiusKm { get; init; }
}
