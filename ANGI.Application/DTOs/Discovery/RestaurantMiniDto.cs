namespace ANGI.Application.DTOs.Discovery;

/// <summary>Represents the compact restaurant data embedded in a dish card.</summary>
public sealed class RestaurantMiniDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ProvinceName { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public decimal RatingAvg { get; set; }
}
