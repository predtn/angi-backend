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
    private readonly ISubmitVerificationUseCase _submitVerificationUseCase;
    private readonly IGetVerificationStatusUseCase _getVerificationStatusUseCase;

    /// <summary>Initializes the HTTP boundary with owner restaurant profile use cases.</summary>
    public OwnerRestaurantController(
        IRegisterRestaurantUseCase registerRestaurantUseCase,
        IGetOwnerRestaurantUseCase getOwnerRestaurantUseCase,
        IUpdateOwnerRestaurantUseCase updateOwnerRestaurantUseCase,
        ISubmitVerificationUseCase submitVerificationUseCase,
        IGetVerificationStatusUseCase getVerificationStatusUseCase)
    {
        _registerRestaurantUseCase = registerRestaurantUseCase;
        _getOwnerRestaurantUseCase = getOwnerRestaurantUseCase;
        _updateOwnerRestaurantUseCase = updateOwnerRestaurantUseCase;
        _submitVerificationUseCase = submitVerificationUseCase;
        _getVerificationStatusUseCase = getVerificationStatusUseCase;
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

    /// <summary>Receives OWN-06 and creates the authenticated owner's first verification submission.</summary>
    [HttpPost("verifications")]
    [ProducesResponseType(typeof(ApiResponse<VerificationDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitVerification(
        SubmitVerificationRequestDto request,
        CancellationToken ct)
    {
        var result = await _submitVerificationUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<VerificationDto>
        {
            Success = true,
            Data = result
        });
    }

    /// <summary>Receives OWN-07 and returns the authenticated owner's verification status and history.</summary>
    [HttpGet("verifications")]
    [Authorize(Roles = "RESTAURANT_OWNER")]
    [ProducesResponseType(typeof(ApiResponse<GetVerificationStatusResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVerifications(CancellationToken ct)
    {
        var result = await _getVerificationStatusUseCase.ExecuteAsync(ct);
        return Ok(new ApiResponse<GetVerificationStatusResponseDto>
        {
            Success = true,
            Data = result
        });
    }
}
