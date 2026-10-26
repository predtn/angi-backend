using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Restaurant;

/// <summary>Exposes restaurant review management operations for the authenticated restaurant owner.</summary>
[ApiController]
[Route("api/v1/owner/restaurant/reviews")]
[Authorize(Roles = "RESTAURANT_OWNER")]
public sealed class OwnerRestaurantReviewsController : ControllerBase
{
    private readonly IGetOwnerReviewsUseCase _getOwnerReviewsUseCase;
    private readonly IReplyReviewUseCase _replyReviewUseCase;

    /// <summary>Initializes the controller with required use cases.</summary>
    public OwnerRestaurantReviewsController(
        IGetOwnerReviewsUseCase getOwnerReviewsUseCase,
        IReplyReviewUseCase replyReviewUseCase)
    {
        _getOwnerReviewsUseCase = getOwnerReviewsUseCase;
        _replyReviewUseCase = replyReviewUseCase;
    }

    /// <summary>Receives OWN-19 and returns paginated reviews for the authenticated owner's restaurant.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<ReviewDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReviews(
        [FromQuery] GetOwnerReviewsRequestDto request,
        CancellationToken ct)
    {
        var result = await _getOwnerReviewsUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<Paged<ReviewDto>> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-20 and creates or updates a reply to a review for the authenticated owner's restaurant.</summary>
    [HttpPut("{reviewId:long}/reply")]
    [ProducesResponseType(typeof(ApiResponse<ReviewReplyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reply(
        long reviewId,
        [FromBody] ReplyReviewRequestDto request,
        CancellationToken ct)
    {
        var result = await _replyReviewUseCase.ExecuteAsync(reviewId, request, ct);
        return Ok(new ApiResponse<ReviewReplyDto> { Success = true, Data = result });
    }
}
