namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Request body of RM-07.</summary>
public sealed class AddRoadmapItemRequestDto
{
    public int DishId { get; init; }

    /// <summary>breakfast | lunch | dinner | snack | late_night.</summary>
    public string MealSlot { get; init; } = null!;

    /// <summary>Order within the meal; null appends the item to that meal.</summary>
    public short? SortOrder { get; init; }

    /// <summary>RM-06 requestId when the selected dish came from suggestions.</summary>
    public Guid? RecoRequestId { get; init; }
}
