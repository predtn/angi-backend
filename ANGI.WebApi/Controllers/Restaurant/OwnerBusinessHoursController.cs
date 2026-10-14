using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Restaurant;

/// <summary>Exposes the owner's weekly business hours (OWN-04).</summary>
[ApiController]
[Route("api/v1/owner/restaurant/business-hours")]
[Authorize(Roles = "RESTAURANT_OWNER")]
public sealed class OwnerBusinessHoursController : ControllerBase
{
    private readonly IUpdateBusinessHoursUseCase _updateBusinessHoursUseCase;

    /// <summary>Initializes the HTTP boundary with the business-hours use case.</summary>
    public OwnerBusinessHoursController(IUpdateBusinessHoursUseCase updateBusinessHoursUseCase)
    {
        _updateBusinessHoursUseCase = updateBusinessHoursUseCase;
    }

    /// <summary>Receives OWN-04 and replaces the whole weekly schedule.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(ApiResponse<UpdateBusinessHoursResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        [FromBody, BindRequired] UpdateBusinessHoursRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateBusinessHoursUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<UpdateBusinessHoursResponseDto> { Success = true, Data = result });
    }
}
