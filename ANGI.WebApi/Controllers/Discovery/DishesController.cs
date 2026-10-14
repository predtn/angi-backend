using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Discovery;

/// <summary>Exposes public dish endpoints (DISH-01, DISH-02).</summary>
[ApiController]
[Route("api/v1/dishes")]
public sealed class DishesController : ControllerBase
{
    private readonly IBrowseDishesUseCase _browseDishesUseCase;
    private readonly IGetDishDetailUseCase _getDishDetailUseCase;

    /// <summary>Initializes the HTTP boundary with the dish use cases.</summary>
    public DishesController(IBrowseDishesUseCase browseDishesUseCase, IGetDishDetailUseCase getDishDetailUseCase)
    {
        _browseDishesUseCase = browseDishesUseCase;
        _getDishDetailUseCase = getDishDetailUseCase;
    }

    /// <summary>Receives DISH-01 and returns one page of dishes ranked by reco.</summary>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<BrowseDishesResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Browse([FromQuery] BrowseDishesRequestDto request, CancellationToken ct)
    {
        var result = await _browseDishesUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<BrowseDishesResponseDto> { Success = true, Data = result });
    }

    /// <summary>Receives DISH-02 and returns a public dish with its restaurant.</summary>
    [AllowAnonymous]
    [HttpGet("{dishId:int}")]
    [ProducesResponseType(typeof(ApiResponse<DishDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail([FromRoute] int dishId, CancellationToken ct)
    {
        var result = await _getDishDetailUseCase.ExecuteAsync(dishId, ct);
        return Ok(new ApiResponse<DishDetailDto> { Success = true, Data = result });
    }
}
