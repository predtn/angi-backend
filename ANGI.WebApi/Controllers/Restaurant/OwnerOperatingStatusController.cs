using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Restaurant;

/// <summary>Exposes the owner's restaurant operating status (OWN-05).</summary>
[ApiController]
[Route("api/v1/owner/restaurant/operating-status")]
[Authorize(Roles = "RESTAURANT_OWNER")]
public sealed class OwnerOperatingStatusController : ControllerBase
{
    private readonly IUpdateOperatingStatusUseCase _updateOperatingStatusUseCase;

    /// <summary>Initializes the HTTP boundary with the operating-status use case.</summary>
    public OwnerOperatingStatusController(IUpdateOperatingStatusUseCase updateOperatingStatusUseCase)
    {
        _updateOperatingStatusUseCase = updateOperatingStatusUseCase;
    }

    /// <summary>Receives OWN-05 and changes the restaurant's operating status.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(ApiResponse<UpdateOperatingStatusResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        [FromBody, BindRequired] UpdateOperatingStatusRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateOperatingStatusUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<UpdateOperatingStatusResponseDto> { Success = true, Data = result });
    }
}
