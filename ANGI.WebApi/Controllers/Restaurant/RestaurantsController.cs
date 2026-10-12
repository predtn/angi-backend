using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Restaurant;

/// <summary>Exposes public endpoints for searching and browsing restaurants.</summary>
[ApiController]
[Route("api/v1/restaurants")]
public sealed class RestaurantsController : ControllerBase
{
    private readonly IBrowseRestaurantsUseCase _browseRestaurantsUseCase;

    /// <summary>Initializes the controller with the restaurant browsing use case.</summary>
    public RestaurantsController(IBrowseRestaurantsUseCase browseRestaurantsUseCase)
    {
        _browseRestaurantsUseCase = browseRestaurantsUseCase;
    }

    // REST-01
    /// <summary>Receives a REST-01 request, invokes BrowseRestaurantsUseCase, and returns paged restaurant cards.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<RestaurantCardDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Browse(
        [FromQuery] BrowseRestaurantsRequestDto request,
        CancellationToken ct)
    {
        var result = await _browseRestaurantsUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status200OK, new ApiResponse<Paged<RestaurantCardDto>>
        {
            Success = true,
            Data = result
        });
    }
}
