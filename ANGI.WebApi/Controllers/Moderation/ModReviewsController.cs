using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes restaurant review and reply moderation endpoints to Mod and Admin accounts.</summary>
[ApiController]
[Route("api/v1/mod/reviews")]
[Authorize(Roles = "MODERATOR,ADMIN,MOD")]
public sealed class ModReviewsController(
    IGetModReviewsUseCase getReviews,
    IUpdateReviewStatusUseCase updateStatus,
    IUpdateReviewReplyStatusUseCase updateReplyStatus) : ControllerBase
{
    /// <summary>MOD-F1: retrieves restaurant reviews with moderation filters and pagination.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<ModReviewDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List([FromQuery] GetModReviewsRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<Paged<ModReviewDto>> { Success = true, Data = await getReviews.ExecuteAsync(request, ct) });

    /// <summary>MOD-F2: changes the moderation status of a review (visible, hidden, removed).</summary>
    [HttpPut("{reviewId:long}/status")]
    [ProducesResponseType(typeof(ApiResponse<UpdateReviewStatusResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(long reviewId, [FromBody] UpdateReviewStatusRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<UpdateReviewStatusResponseDto> { Success = true, Data = await updateStatus.ExecuteAsync(reviewId, request, ct) });

    /// <summary>MOD-F3: changes the moderation status of an owner reply to a review.</summary>
    [HttpPut("{reviewId:long}/reply/status")]
    [ProducesResponseType(typeof(ApiResponse<UpdateReviewReplyStatusResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateReplyStatus(long reviewId, [FromBody] UpdateReviewReplyStatusRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<UpdateReviewReplyStatusResponseDto> { Success = true, Data = await updateReplyStatus.ExecuteAsync(reviewId, request, ct) });
}
