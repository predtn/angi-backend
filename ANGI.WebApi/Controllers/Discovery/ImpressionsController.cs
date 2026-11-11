using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Discovery;

/// <summary>Receives the dishes a Traveler really saw (IMP-01).</summary>
[ApiController]
[Route("api/v1/impressions")]
[Authorize(Roles = "TRAVELER")]
public sealed class ImpressionsController : ControllerBase
{
    private readonly IRecordImpressionsUseCase _recordImpressionsUseCase;

    /// <summary>Initializes the HTTP boundary with the impressions use case.</summary>
    public ImpressionsController(IRecordImpressionsUseCase recordImpressionsUseCase)
    {
        _recordImpressionsUseCase = recordImpressionsUseCase;
    }

    /// <summary>Receives IMP-01 and forwards the impressions to reco; answers 202 even when reco fails.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<RecordImpressionsResponseDto>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Record(
        [FromBody, BindRequired] RecordImpressionsRequestDto request,
        CancellationToken ct)
    {
        var result = await _recordImpressionsUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status202Accepted, new ApiResponse<RecordImpressionsResponseDto>
        {
            Success = true,
            Data = result
        });
    }
}
