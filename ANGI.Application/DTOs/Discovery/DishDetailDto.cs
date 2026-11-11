using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.DTOs.Discovery;

/// <summary>
/// DishDetail of the API Design: every DishCard field, then the detail fields. It does not inherit DishCardDto because
/// its restaurant is the full RestaurantCard, not the RestaurantMini of a card.
/// </summary>
public sealed class DishDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string? CoverUrl { get; set; }
    public string DishForm { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public int LikeCount { get; set; }
    public int DislikeCount { get; set; }
    public IReadOnlyList<TagDto> Tags { get; set; } = Array.Empty<TagDto>();

    /// <summary>Always null: DISH-02 does not go through reco.</summary>
    public int? Rank { get; set; }

    public string? Description { get; set; }
    public IReadOnlyList<string> Images { get; set; } = Array.Empty<string>();

    /// <summary>active | hidden | removed; DISH-02 only returns active dishes.</summary>
    public string Status { get; set; } = string.Empty;

    public RestaurantCardDto Restaurant { get; set; } = new();
}
