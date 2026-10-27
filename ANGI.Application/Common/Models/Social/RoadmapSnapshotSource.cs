using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Social;

/// <summary>Contains only owned-roadmap columns required to create a blog snapshot.</summary>
public sealed class RoadmapSnapshotSource
{
    public long RoadmapId { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateOnly? StartDate { get; init; }
    public short NumDays { get; init; }
    public IReadOnlyList<RoadmapSnapshotDaySource> Days { get; init; } = [];
}

/// <summary>Contains one day and its already ordered snapshot item projections.</summary>
public sealed class RoadmapSnapshotDaySource
{
    public short DayNumber { get; init; }
    public IReadOnlyList<RoadmapSnapshotItemSource> Items { get; init; } = [];
}

/// <summary>Contains only dish and restaurant values copied into one blog snapshot item.</summary>
public sealed class RoadmapSnapshotItemSource
{
    public MealSlot MealSlot { get; init; }
    public int DishId { get; init; }
    public string DishName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public long RestaurantId { get; init; }
    public string RestaurantName { get; init; } = string.Empty;
    public string AddressLine { get; init; } = string.Empty;
    public string? Ward { get; init; }
    public string? ProvinceName { get; init; }
}
