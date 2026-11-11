using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Adds moderation states and the number of open reports to RestaurantCard.</summary>
public sealed class ModRestaurantCardDto : RestaurantCardDto
{
    public string VerificationStatus { get; init; } = string.Empty;
    public string ModerationStatus { get; init; } = string.Empty;
    public int PendingReportCount { get; init; }
}
