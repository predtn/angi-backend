namespace ANGI.Application.DTOs.Discovery;

/// <summary>Request body of DISH-03.</summary>
public sealed class RollDishRequestDto
{
    /// <summary>Case-insensitive substring of restaurants.province_name.</summary>
    public string? Province { get; set; }

    /// <summary>The dish needs at least one of these tags.</summary>
    public short[]? TagIds { get; set; }

    /// <summary>Dishes with any of these tags are left out.</summary>
    public short[]? ExcludeTagIds { get; set; }

    /// <summary>breakfast | lunch | dinner | snack: the dish needs that meal_time tag.</summary>
    public string? MealTime { get; set; }

    public decimal? MaxPrice { get; set; }

    /// <summary>Dishes already rolled in the session (≤ 50), so a roll does not repeat.</summary>
    public int[]? ExcludeDishIds { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>0.5..50, needs latitude and longitude; 3 km when they come without it.</summary>
    public decimal? RadiusKm { get; set; }
}
