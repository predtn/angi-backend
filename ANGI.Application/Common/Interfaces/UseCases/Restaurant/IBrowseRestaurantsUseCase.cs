using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Executes the REST-01 restaurant browsing and search use case.</summary>
public interface IBrowseRestaurantsUseCase
{
    /// <summary>Browses restaurants using the specified filters and pagination criteria.</summary>
    Task<Paged<RestaurantCardDto>> ExecuteAsync(BrowseRestaurantsRequestDto request, CancellationToken ct);
}
