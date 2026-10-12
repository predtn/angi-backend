using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.Browse;

/// <summary>Coordinates validation, actor detection, and query execution for REST-01 restaurant browsing.</summary>
public sealed class BrowseRestaurantsUseCase : IBrowseRestaurantsUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IValidator<BrowseRestaurantsRequestDto> _validator;
    private readonly TimeProvider _timeProvider;
    private readonly ICurrentUserService _currentUserService;

    /// <summary>Initializes dependencies required for restaurant browsing.</summary>
    public BrowseRestaurantsUseCase(
        IRestaurantRepository restaurantRepository,
        IValidator<BrowseRestaurantsRequestDto> validator,
        TimeProvider timeProvider,
        ICurrentUserService currentUserService)
    {
        _restaurantRepository = restaurantRepository;
        _validator = validator;
        _timeProvider = timeProvider;
        _currentUserService = currentUserService;
    }

    /// <summary>Executes the restaurant browsing operation after validating query parameters.</summary>
    public async Task<Paged<RestaurantCardDto>> ExecuteAsync(
        BrowseRestaurantsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        // Detect optional authenticated actor (Guest vs Traveler)
        var isTraveler = string.Equals(_currentUserService.Role, "TRAVELER", StringComparison.OrdinalIgnoreCase);

        var currentUtc = _timeProvider.GetUtcNow();
        return await _restaurantRepository.BrowseAsync(request, currentUtc, ct);
    }
}
