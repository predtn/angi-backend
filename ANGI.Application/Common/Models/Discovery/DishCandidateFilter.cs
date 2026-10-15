using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Discovery;

/// <summary>
/// The candidate filter of DISH-01 (query A1). Candidates are always active, available dishes of open restaurants that
/// are verified, visible and not deleted.
/// </summary>
public sealed record DishCandidateFilter
{
    /// <summary>Case-insensitive substring of the dish name or the restaurant name.</summary>
    public string? Query { get; init; }

    /// <summary>Case-insensitive substring of the restaurant province.</summary>
    public string? Province { get; init; }

    /// <summary>Case-insensitive substring of the restaurant district.</summary>
    public string? District { get; init; }

    /// <summary>Within one tag type a dish needs one of the tags; across types it needs every type.</summary>
    public IReadOnlyList<short> TagIds { get; init; } = [];

    /// <summary>A dish needs at least one of these tags, whatever their type (DISH-03 tagIds).</summary>
    public IReadOnlyList<short> AnyTagIds { get; init; } = [];

    /// <summary>A dish needs the meal_time tag with this code (DISH-03 mealTime: breakfast, lunch, dinner, snack).</summary>
    public string? MealTimeCode { get; init; }

    /// <summary>Dishes left out by id (DISH-03: dishes already rolled in the session).</summary>
    public IReadOnlyList<int> ExcludeDishIds { get; init; } = [];

    /// <summary>A dish with any of these tags is left out (dietary constraints sent by the app).</summary>
    public IReadOnlyList<short> ExcludeTagIds { get; init; } = [];

    public DishForm? DishForm { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public long? RestaurantId { get; init; }
    public NearbyArea? Area { get; init; }
}
