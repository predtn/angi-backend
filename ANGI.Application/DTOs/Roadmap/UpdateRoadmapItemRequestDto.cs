namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Request body of RM-08 for replacing or repositioning one roadmap item.</summary>
public sealed class UpdateRoadmapItemRequestDto
{
    /// <summary>Replacement dish; when supplied, RM-08 creates a new item id.</summary>
    public int? DishId { get; init; }

    /// <summary>RM-06 requestId when the replacement dish came from suggestions.</summary>
    public Guid? RecoRequestId { get; init; }

    /// <summary>Destination day in the same roadmap.</summary>
    public long? DayId { get; init; }

    /// <summary>breakfast | lunch | dinner | snack | late_night.</summary>
    public string? MealSlot { get; init; }

    /// <summary>Order within the destination meal.</summary>
    public short? SortOrder { get; init; }
}
