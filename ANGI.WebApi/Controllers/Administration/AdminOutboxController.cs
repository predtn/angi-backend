using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Administration;

/// <summary>Exposes outbox event monitoring and operations for administrators.</summary>
[Authorize(Roles = "ADMIN")]
[ApiController]
[Route("api/v1/admin/outbox")]
public sealed class AdminOutboxController : ControllerBase
{
    private readonly IGetFailedOutboxMessagesUseCase _getFailedOutboxMessagesUseCase;

    /// <summary>Initializes the controller with the outbox query use case.</summary>
    public AdminOutboxController(IGetFailedOutboxMessagesUseCase getFailedOutboxMessagesUseCase)
    {
        _getFailedOutboxMessagesUseCase = getFailedOutboxMessagesUseCase;
    }

    // OPS-01
    /// <summary>Retrieves a paginated list of failed/dead or pending outbox messages.</summary>
    /// <param name="request">Contains status and eventType filters, and page and pageSize parameters.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The paginated list of outbox messages was successfully returned.</response>
    /// <response code="400">The query parameters failed validation.</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="500">An unexpected server error occurred.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<OutboxMessageDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetFailedOutboxMessages([FromQuery] GetOutboxMessagesRequestDto request, CancellationToken ct)
    {
        var result = await _getFailedOutboxMessagesUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<Paged<OutboxMessageDto>> { Success = true, Data = result });
    }
}
