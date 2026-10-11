using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents a menu draft or a submitted menu revision.</summary>
public class MenuSubmissionDto
{
    public long Id { get; set; }
    public long RestaurantId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? OwnerNote { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public UserSummaryDto? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Represents a menu submission together with its editable items.</summary>
public class MenuSubmissionWithItemsDto : MenuSubmissionDto
{
    public IReadOnlyList<MenuSubmissionItemDto> Items { get; set; } = Array.Empty<MenuSubmissionItemDto>();
}

/// <summary>Represents a menu submission detail and dishes removed by that revision.</summary>
public sealed class MenuSubmissionDetailDto : MenuSubmissionWithItemsDto
{
    public IReadOnlyList<DishCardDto> RemovedDishes { get; set; } = Array.Empty<DishCardDto>();
}
