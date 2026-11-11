namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents one opening interval in Vietnam local time.</summary>
public sealed class BusinessHourDto
{
    public short DayOfWeek { get; set; }
    public string OpenTime { get; set; } = string.Empty;
    public string CloseTime { get; set; } = string.Empty;
}
