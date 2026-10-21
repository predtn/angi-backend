namespace ANGI.Application.DTOs.Social;

/// <summary>Ảnh chụp roadmap lưu trong blogs.roadmap_snapshot.</summary>
public sealed class RoadmapSnapshotDto
{
    public long RoadmapId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public short NumDays { get; set; }
    public decimal TotalCost { get; set; }
    public DateTime CapturedAt { get; set; }
    public List<RoadmapSnapshotDayDto> Days { get; set; } = [];
}

public sealed class RoadmapSnapshotDayDto
{
    public short DayNumber { get; set; }
    public List<RoadmapSnapshotItemDto> Items { get; set; } = [];
}

public sealed class RoadmapSnapshotItemDto
{
    public string MealSlot { get; set; } = string.Empty;
    public int DishId { get; set; }
    public string DishName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public long RestaurantId { get; set; }
    public string RestaurantName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}
