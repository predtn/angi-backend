using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Restaurant;

/// <summary>Exposes public endpoints for searching and viewing restaurant details, menu, and reviews.</summary>
[ApiController]
[Route("api/v1/restaurants")]
public sealed class RestaurantsController : ControllerBase
{
    private readonly IBrowseRestaurantsUseCase _browseRestaurantsUseCase;
    private readonly IGetRestaurantDetailUseCase _getRestaurantDetailUseCase;
    private readonly IGetRestaurantMenuUseCase _getRestaurantMenuUseCase;
    private readonly IGetRestaurantReviewsUseCase _getRestaurantReviewsUseCase;

    /// <summary>Initializes the controller with restaurant use cases.</summary>
    public RestaurantsController(
        IBrowseRestaurantsUseCase browseRestaurantsUseCase,
        IGetRestaurantDetailUseCase getRestaurantDetailUseCase,
        IGetRestaurantMenuUseCase getRestaurantMenuUseCase,
        IGetRestaurantReviewsUseCase getRestaurantReviewsUseCase)
    {
        _browseRestaurantsUseCase = browseRestaurantsUseCase;
        _getRestaurantDetailUseCase = getRestaurantDetailUseCase;
        _getRestaurantMenuUseCase = getRestaurantMenuUseCase;
        _getRestaurantReviewsUseCase = getRestaurantReviewsUseCase;
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

    // REST-02
    /// <summary>Receives a REST-02 request, invokes GetRestaurantDetailUseCase, and returns restaurant details.</summary>
    [HttpGet("{restaurantId:long}")]
    [ProducesResponseType(typeof(ApiResponse<RestaurantDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(
        [FromRoute] long restaurantId,
        [FromQuery] decimal? lat,
        [FromQuery] decimal? lng,
        [FromQuery] string? include,
        CancellationToken ct)
    {
        var request = new GetRestaurantDetailRequestDto
        {
            RestaurantId = restaurantId,
            Lat = lat,
            Lng = lng,
            Include = include
        };
        var result = await _getRestaurantDetailUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status200OK, new ApiResponse<RestaurantDetailDto>
        {
            Success = true,
            Data = result
        });
    }

    // REST-03
    /// <summary>Receives a REST-03 request, invokes GetRestaurantMenuUseCase, and returns active menu dishes.</summary>
    [HttpGet("{restaurantId:long}/menu")]
    [ProducesResponseType(typeof(ApiResponse<RestaurantMenuDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMenu(
        [FromRoute] long restaurantId,
        [FromQuery] short[]? tagIds,
        [FromQuery] bool includeUnavailable = true,
        CancellationToken ct = default)
    {
        var request = new GetRestaurantMenuRequestDto
        {
            RestaurantId = restaurantId,
            TagIds = tagIds,
            IncludeUnavailable = includeUnavailable
        };
        var result = await _getRestaurantMenuUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status200OK, new ApiResponse<RestaurantMenuDto>
        {
            Success = true,
            Data = result
        });
    }

    // REST-04
    /// <summary>Receives a REST-04 request, invokes GetRestaurantReviewsUseCase, and returns paginated reviews.</summary>
    [HttpGet("{restaurantId:long}/reviews")]
    [ProducesResponseType(typeof(ApiResponse<RestaurantReviewsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReviews(
        [FromRoute] long restaurantId,
        [FromQuery] short? rating,
        [FromQuery] bool? hasImage,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var request = new GetRestaurantReviewsRequestDto
        {
            RestaurantId = restaurantId,
            Rating = rating,
            HasImage = hasImage,
            Sort = sort,
            Page = page,
            PageSize = pageSize
        };
        var result = await _getRestaurantReviewsUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status200OK, new ApiResponse<RestaurantReviewsDto>
        {
            Success = true,
            Data = result
        });
    }
}
