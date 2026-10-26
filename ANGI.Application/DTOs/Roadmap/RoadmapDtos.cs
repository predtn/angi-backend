using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Represents the overview section shared by roadmap cards and details.</summary>
public sealed class RoadmapSummaryDto
{
    public long Id { get; set; }
    public long Version { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public short NumDays { get; set; }
    public decimal? BudgetAmount { get; set; }
    public decimal TotalEstimatedCost { get; set; }
    public int ItemCount { get; set; }
    public string? CoverUrl { get; set; }
    public string OriginMode { get; set; } = string.Empty;
    public decimal? OriginLatitude { get; set; }
    public decimal? OriginLongitude { get; set; }
    public string? OriginLabel { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Represents a dish embedded in a roadmap item, including inactive dishes.</summary>
public sealed class RoadmapDishDto : DishCardDto
{
    public string Status { get; set; } = string.Empty;
}

/// <summary>Represents one scheduled dish in a roadmap day.</summary>
public sealed class RoadmapItemDto
{
    public long Id { get; set; }
    public long DayId { get; set; }
    public short DayNumber { get; set; }
    public string MealSlot { get; set; } = string.Empty;
    public short SortOrder { get; set; }
    public string Source { get; set; } = string.Empty;
    public Guid? RecoRequestId { get; set; }
    public RoadmapDishDto Dish { get; set; } = new();
    public string? MyFeedback { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Represents one day and its ordered roadmap items.</summary>
public sealed class RoadmapDayDto
{
    public long DayId { get; set; }
    public short DayNumber { get; set; }
    public DateOnly? Date { get; set; }
    public string? Note { get; set; }
    public decimal DayCost { get; set; }
    public IReadOnlyList<RoadmapItemDto> Items { get; set; } = Array.Empty<RoadmapItemDto>();
}

/// <summary>Represents a cost subtotal for one roadmap day.</summary>
public sealed class RoadmapDayCostDto
{
    public short DayNumber { get; set; }
    public decimal Cost { get; set; }
}

/// <summary>Represents a cost subtotal for one meal slot.</summary>
public sealed class RoadmapMealSlotCostDto
{
    public string MealSlot { get; set; } = string.Empty;
    public decimal Cost { get; set; }
}

/// <summary>Represents the computed budget section of a roadmap detail.</summary>
public sealed class RoadmapBudgetDto
{
    public decimal? BudgetAmount { get; set; }
    public decimal TotalEstimatedCost { get; set; }
    public decimal? Remaining { get; set; }
    public bool IsOverBudget { get; set; }
    public IReadOnlyList<RoadmapDayCostDto> ByDay { get; set; } = Array.Empty<RoadmapDayCostDto>();
    public IReadOnlyList<RoadmapMealSlotCostDto> ByMealSlot { get; set; } = Array.Empty<RoadmapMealSlotCostDto>();
}

/// <summary>Represents the complete Overview, Itinerary, and Budget response.</summary>
public sealed class RoadmapDetailDto
{
    public RoadmapSummaryDto Overview { get; set; } = new();
    public IReadOnlyList<RoadmapDayDto> Itinerary { get; set; } = Array.Empty<RoadmapDayDto>();
    public RoadmapBudgetDto Budget { get; set; } = new();
}
