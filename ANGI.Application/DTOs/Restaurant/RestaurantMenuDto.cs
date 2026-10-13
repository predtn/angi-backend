using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents the active menu dishes of a restaurant (REST-03).</summary>
public sealed class RestaurantMenuDto
{
    public long RestaurantId { get; init; }
    public IReadOnlyList<DishCardDto> Dishes { get; init; } = Array.Empty<DishCardDto>();
}
