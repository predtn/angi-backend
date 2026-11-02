using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Represents a review in the moderator queue (MOD-F1) including restaurant summary.</summary>
public sealed class ModReviewDto : ReviewDto
{
    public RestaurantMiniDto Restaurant { get; init; } = new();
}
