namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains an active or hidden dish state and the moderator's explanation.</summary>
public sealed class UpdateDishModerationStatusRequestDto
{
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Returns the persisted dish moderation state for MOD-R4.</summary>
public sealed class UpdateDishModerationStatusResponseDto
{
    public int DishId { get; init; }
    public string Status { get; init; } = string.Empty;
}
