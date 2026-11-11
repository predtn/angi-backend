namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Response data of OWN-04.</summary>
public sealed class UpdateBusinessHoursResponseDto
{
    /// <summary>The saved schedule, ordered by day then opening time.</summary>
    public IReadOnlyList<BusinessHourDto> Hours { get; set; } = [];

    /// <summary>Recomputed from the saved schedule and the current Vietnam time.</summary>
    public bool IsOpenNow { get; set; }
}
