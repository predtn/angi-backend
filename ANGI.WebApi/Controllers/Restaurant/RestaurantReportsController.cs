using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Restaurant;

/// <summary>Exposes restaurant reporting operations for travelers.</summary>
[ApiController]
[Authorize(Roles = "TRAVELER,Traveler")]
public sealed class RestaurantReportsController : ControllerBase
{
    private readonly ICreateRestaurantReportUseCase _createRestaurantReportUseCase;

    /// <summary>Initializes the HTTP boundary with the REP-01 use case.</summary>
    public RestaurantReportsController(ICreateRestaurantReportUseCase createRestaurantReportUseCase)
    {
        _createRestaurantReportUseCase = createRestaurantReportUseCase;
    }

    // REP-01
    /// <summary>Báo cáo nhà hàng sai thông tin hoặc vi phạm.</summary>
    [HttpPost("api/v1/restaurants/{restaurantId:long}/reports")]
    [ProducesResponseType(typeof(ApiResponse<CreateRestaurantReportResponseDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromRoute] long restaurantId,
        [FromBody, BindRequired] CreateRestaurantReportRequestDto request,
        CancellationToken ct)
    {
        var result = await _createRestaurantReportUseCase.ExecuteAsync(restaurantId, request, ct);
        return StatusCode(
            StatusCodes.Status201Created,
            new ApiResponse<CreateRestaurantReportResponseDto> { Success = true, Data = result });
    }
}
