namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Encapsulates path and query parameters for REST-03 restaurant menu retrieval.</summary>
public sealed class GetRestaurantMenuRequestDto
{
    public long RestaurantId { get; init; }
    public IReadOnlyList<short>? TagIds { get; init; }
    public bool IncludeUnavailable { get; init; } = true;
}
