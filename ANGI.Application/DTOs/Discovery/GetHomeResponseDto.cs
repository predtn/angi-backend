using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.DTOs.Social;

namespace ANGI.Application.DTOs.Discovery;

/// <summary>Response data of HOME-01. Banners are hardcoded by the frontend.</summary>
public sealed class GetHomeResponseDto
{
    /// <summary>Most liked in 7 days, completed by like_count; not ranked by reco (rank = null).</summary>
    public IReadOnlyList<DishCardDto> TrendingDishes { get; init; } = [];

    public IReadOnlyList<RestaurantCardDto> TopRestaurants { get; init; } = [];
    public IReadOnlyList<BlogCardDto> FeaturedBlogs { get; init; } = [];
    public HomeRecommendedDto Recommended { get; init; } = new();
}

/// <summary>The "Recommended for you" block, ranked by reco with context = home.</summary>
public sealed class HomeRecommendedDto
{
    /// <summary>Reco's request_id for IMP-01; null for a Guest or when reco failed.</summary>
    public Guid? RequestId { get; init; }

    public IReadOnlyList<DishCardDto> Items { get; init; } = [];
}
