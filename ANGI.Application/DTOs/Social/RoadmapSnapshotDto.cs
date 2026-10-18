namespace ANGI.Application.DTOs.Social;

/// <summary>Immutable roadmap data embedded in a blog at sharing time.</summary>
public sealed class RoadmapSnapshotDto
{
    public long RoadmapId { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateOnly? StartDate { get; init; }
    public short NumDays { get; init; }
    public decimal TotalCost { get; init; }
    public DateTime CapturedAt { get; init; }
    public IReadOnlyList<RoadmapSnapshotDayDto> Days { get; init; } = [];
}

/// <summary>One ordered day in a shared roadmap snapshot.</summary>
public sealed class RoadmapSnapshotDayDto
{
    public short DayNumber { get; init; }
    public IReadOnlyList<RoadmapSnapshotItemDto> Items { get; init; } = [];
}

/// <summary>Dish, restaurant, and captured-price data for one shared roadmap item.</summary>
public sealed class RoadmapSnapshotItemDto
{
    public string MealSlot { get; init; } = string.Empty;
    public int DishId { get; init; }
    public string DishName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public long RestaurantId { get; init; }
    public string RestaurantName { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
}
