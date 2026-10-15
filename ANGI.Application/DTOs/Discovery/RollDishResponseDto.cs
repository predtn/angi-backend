namespace ANGI.Application.DTOs.Discovery;

/// <summary>Response data of DISH-03.</summary>
public sealed class RollDishResponseDto
{
    /// <summary>dish_rolls.id.</summary>
    public long RollId { get; init; }

    /// <summary>Reco's request_id (context = roll); null when reco failed and the roll used the fallback order.</summary>
    public Guid? RequestId { get; init; }

    /// <summary>The rolled dish; rank is its place in reco's top 10, distanceKm is set when coordinates were sent.</summary>
    public DishDetailDto Dish { get; init; } = new();
}
