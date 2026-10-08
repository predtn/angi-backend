namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains the restaurant profile submitted by OWN-01.</summary>
public sealed class RegisterRestaurantRequestDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string AddressLine { get; set; } = string.Empty;
    public string? Ward { get; set; }
    public string District { get; set; } = string.Empty;
    public string ProvinceName { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public short? PriceLevel { get; set; }
    public long? CoverMediaId { get; set; }
    public IReadOnlyList<long>? ImageMediaIds { get; set; }
    public IReadOnlyList<BusinessHourDto>? BusinessHours { get; set; }
}
