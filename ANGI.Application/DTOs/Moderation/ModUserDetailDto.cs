using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Represents detailed user moderation profile with sanctions history, content stats and restaurant.</summary>
public sealed class ModUserDetailDto : AdminUserCardDto
{
    public IReadOnlyList<SanctionDto> Sanctions { get; init; } = [];
    public int BlogCount { get; init; }
    public int ReviewCount { get; init; }
    public int ReportsAgainstContent { get; init; }
    public RestaurantCardDto? Restaurant { get; init; }
}
