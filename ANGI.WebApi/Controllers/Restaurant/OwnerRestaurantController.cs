using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Restaurant;

/// <summary>Exposes restaurant profile operations reserved for restaurant owners.</summary>
[ApiController]
[Route("api/v1/owner/restaurant")]
[Authorize(Roles = "RESTAURANT_OWNER")]
public sealed class OwnerRestaurantController : ControllerBase
{
    private readonly IRegisterRestaurantUseCase _registerRestaurantUseCase;
    private readonly IGetOwnerRestaurantUseCase _getOwnerRestaurantUseCase;
    private readonly IUpdateOwnerRestaurantUseCase _updateOwnerRestaurantUseCase;

    /// <summary>Initializes the HTTP boundary with owner restaurant profile use cases.</summary>
    public OwnerRestaurantController(
        IRegisterRestaurantUseCase registerRestaurantUseCase,
        IGetOwnerRestaurantUseCase getOwnerRestaurantUseCase,
        IUpdateOwnerRestaurantUseCase updateOwnerRestaurantUseCase)
    {
        _registerRestaurantUseCase = registerRestaurantUseCase;
        _getOwnerRestaurantUseCase = getOwnerRestaurantUseCase;
        _updateOwnerRestaurantUseCase = updateOwnerRestaurantUseCase;
    }

    /// <summary>Receives OWN-01 and creates the authenticated owner's restaurant.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<OwnerRestaurantDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(
        RegisterRestaurantRequestDto request,
        CancellationToken ct)
    {
        var result = await _registerRestaurantUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<OwnerRestaurantDto>
        {
            Success = true,
            Data = result
        });
    }

    /// <summary>Receives OWN-02 and returns the authenticated owner's complete restaurant.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<OwnerRestaurantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var result = await _getOwnerRestaurantUseCase.ExecuteAsync(ct);
        return Ok(new ApiResponse<OwnerRestaurantDto> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-03 and partially updates the authenticated owner's restaurant.</summary>
    [HttpPatch]
    [ProducesResponseType(typeof(ApiResponse<OwnerRestaurantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        UpdateRestaurantRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateOwnerRestaurantUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<OwnerRestaurantDto> { Success = true, Data = result });
    }
}
