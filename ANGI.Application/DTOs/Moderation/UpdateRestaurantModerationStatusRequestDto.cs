namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the new restaurant moderation state and its mandatory explanation.</summary>
public sealed class UpdateRestaurantModerationStatusRequestDto
{
    public string ModerationStatus { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public long? ReportId { get; set; }
}

/// <summary>Returns the persisted restaurant moderation state for MOD-R3.</summary>
public sealed class UpdateRestaurantModerationStatusResponseDto
{
    public long RestaurantId { get; init; }
    public string ModerationStatus { get; init; } = string.Empty;
}
