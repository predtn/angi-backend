namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents the complete owner-facing restaurant profile.</summary>
public sealed class OwnerRestaurantDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string AddressLine { get; set; } = string.Empty;
    public string? District { get; set; }
    public string? ProvinceName { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public short? PriceLevel { get; set; }
    public decimal RatingAvg { get; set; }
    public int RatingCount { get; set; }
    public string OperatingStatus { get; set; } = string.Empty;
    public bool IsOpenNow { get; set; }
    public decimal? DistanceKm { get; set; }
    public string? Description { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? Ward { get; set; }
    public IReadOnlyList<string> Images { get; set; } = Array.Empty<string>();
    public IReadOnlyList<BusinessHourDto> BusinessHours { get; set; } = Array.Empty<BusinessHourDto>();
    public object? Menu { get; set; }
    public object? MyReview { get; set; }
    public string VerificationStatus { get; set; } = string.Empty;
    public string ModerationStatus { get; set; } = string.Empty;
    public VerificationDto? LatestVerification { get; set; }
    public OpenMenuSubmissionDto? OpenMenuSubmission { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
