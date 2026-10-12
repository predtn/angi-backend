namespace ANGI.Application.DTOs.Discovery;

/// <summary>Represents the shared dish card returned by menu and discovery endpoints.</summary>
public class DishCardDto
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
    public RestaurantMiniDto Restaurant { get; set; } = new();
    public int? Rank { get; set; }
}
