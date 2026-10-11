namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains the availability value accepted by OWN-10.</summary>
public sealed class UpdateDishAvailabilityRequestDto
{
    public bool? IsAvailable { get; set; }
}

/// <summary>Represents the persisted availability of an owner dish.</summary>
public sealed class DishAvailabilityDto
{
    public int DishId { get; set; }
    public bool IsAvailable { get; set; }
}
