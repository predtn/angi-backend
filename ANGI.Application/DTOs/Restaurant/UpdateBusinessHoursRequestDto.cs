namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Request body of OWN-04: the complete weekly schedule that replaces the current one.</summary>
public sealed class UpdateBusinessHoursRequestDto
{
    /// <summary>
    /// Every opening interval of the week; an empty list means no hours declared. A day may have several. Nullable so a
    /// missing field reaches the validator (Vietnamese message) instead of MVC's implicit [Required].
    /// </summary>
    public List<BusinessHourDto>? Hours { get; set; }
}
