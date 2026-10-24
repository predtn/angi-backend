using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the flattened MOD-M2 submission, restaurant, and menu comparison.</summary>
public sealed class ModMenuSubmissionDetailDto : MenuSubmissionDto
{
    public RestaurantMiniDto Restaurant { get; set; } = new();
    public IReadOnlyList<ModMenuItemDiffDto> Items { get; set; } = [];
    public IReadOnlyList<DishCardDto> RemovedDishes { get; set; } = [];
}
