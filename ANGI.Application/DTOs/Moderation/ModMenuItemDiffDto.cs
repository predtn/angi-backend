using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Describes a proposed menu item and its current dish for comparison.</summary>
public sealed class ModMenuItemDiffDto : MenuSubmissionItemDto
{
    public DishCardDto? Before { get; set; }
}
