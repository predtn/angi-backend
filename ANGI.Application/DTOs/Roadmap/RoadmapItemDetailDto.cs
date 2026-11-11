using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Represents the complete RM-12 roadmap item, dish, restaurant, feedback, and review response.</summary>
public sealed class RoadmapItemDetailDto
{
    public long Id { get; set; }
    public long DayId { get; set; }
    public short DayNumber { get; set; }
    public string MealSlot { get; set; } = string.Empty;
    public short SortOrder { get; set; }
    public string Source { get; set; } = string.Empty;
    public Guid? RecoRequestId { get; set; }
    public string? MyFeedback { get; set; }
    public DateTime CreatedAt { get; set; }
    public DishDetailDto Dish { get; set; } = new();
    public RestaurantDetailDto Restaurant { get; set; } = new();
    public bool CanRate { get; set; }
    public ReviewDto? MyReview { get; set; }
}
