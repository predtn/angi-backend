namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Response data of OWN-05.</summary>
public sealed class UpdateOperatingStatusResponseDto
{
    /// <summary>restaurants.operating_status after the change.</summary>
    public string OperatingStatus { get; set; } = string.Empty;
}
