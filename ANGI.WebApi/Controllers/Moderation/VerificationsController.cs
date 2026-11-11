using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes restaurant verification review operations exclusively to Mods.</summary>
[ApiController]
[Route("api/v1/mod/verifications")]
[Authorize(Roles = "MOD")]
public sealed class VerificationsController : ControllerBase
{
    private readonly IListVerificationsUseCase _listVerificationsUseCase;
    private readonly IGetVerificationReviewUseCase _getVerificationReviewUseCase;
    private readonly IReviewVerificationUseCase _reviewVerificationUseCase;

    /// <summary>Initializes the HTTP boundary with MOD-V1 through MOD-V3 use cases.</summary>
    public VerificationsController(
        IListVerificationsUseCase listVerificationsUseCase,
        IGetVerificationReviewUseCase getVerificationReviewUseCase,
        IReviewVerificationUseCase reviewVerificationUseCase)
    {
        _listVerificationsUseCase = listVerificationsUseCase;
        _getVerificationReviewUseCase = getVerificationReviewUseCase;
        _reviewVerificationUseCase = reviewVerificationUseCase;
    }

    /// <summary>Receives MOD-V1 and returns the filtered verification review queue.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<ModVerificationCardDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] ListVerificationsRequestDto request,
        CancellationToken ct)
    {
        var result = await _listVerificationsUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<Paged<ModVerificationCardDto>> { Success = true, Data = result });
    }

    /// <summary>Receives MOD-V2 and returns one complete verification review record.</summary>
    [HttpGet("{verificationId:long}")]
    [ProducesResponseType(typeof(ApiResponse<VerificationReviewDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long verificationId, CancellationToken ct)
    {
        var result = await _getVerificationReviewUseCase.ExecuteAsync(verificationId, ct);
        return Ok(new ApiResponse<VerificationReviewDetailDto> { Success = true, Data = result });
    }

    /// <summary>Receives MOD-V3 and atomically decides a pending verification submission.</summary>
    [HttpPost("{verificationId:long}/decision")]
    [ProducesResponseType(typeof(ApiResponse<VerificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decide(
        long verificationId,
        ReviewVerificationRequestDto request,
        CancellationToken ct)
    {
        var result = await _reviewVerificationUseCase.ExecuteAsync(verificationId, request, ct);
        return Ok(new ApiResponse<VerificationDto> { Success = true, Data = result });
    }
}
