using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Represents one MOD-V1 verification queue item.</summary>
public sealed class ModVerificationCardDto : VerificationDto
{
    public RestaurantCardDto Restaurant { get; set; } = new();
}

/// <summary>Represents MOD-V2 verification details with the restaurant and optional preceding submission.</summary>
public sealed class VerificationReviewDetailDto : VerificationDto
{
    public OwnerRestaurantDto Restaurant { get; set; } = new();
    public VerificationDto? Previous { get; set; }
}
