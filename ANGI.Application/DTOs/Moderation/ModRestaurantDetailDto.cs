using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Extends OwnerRestaurant with the owner, running menu, reports and recent audit history.</summary>
public sealed class ModRestaurantDetailDto : OwnerRestaurantDto
{
    public AdminUserCardDto Owner { get; init; } = new();
    public IReadOnlyList<OwnerMenuDishDto> Dishes { get; init; } = [];
    public IReadOnlyList<ReportDto> Reports { get; init; } = [];
    public IReadOnlyList<AuditLogDto> RecentAudit { get; init; } = [];
}
