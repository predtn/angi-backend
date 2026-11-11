using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Discovery;

/// <summary>Exposes the one-time preference survey of a Traveler (PREF-02, PREF-03).</summary>
[ApiController]
[Route("api/v1/me/preference-survey")]
[Authorize(Roles = "TRAVELER")]
public sealed class PreferenceSurveyController : ControllerBase
{
    private readonly ISubmitPreferenceSurveyUseCase _submitPreferenceSurveyUseCase;
    private readonly ISkipPreferenceSurveyUseCase _skipPreferenceSurveyUseCase;

    /// <summary>Initializes the HTTP boundary with the preference survey use cases.</summary>
    public PreferenceSurveyController(
        ISubmitPreferenceSurveyUseCase submitPreferenceSurveyUseCase,
        ISkipPreferenceSurveyUseCase skipPreferenceSurveyUseCase)
    {
        _submitPreferenceSurveyUseCase = submitPreferenceSurveyUseCase;
        _skipPreferenceSurveyUseCase = skipPreferenceSurveyUseCase;
    }

    /// <summary>Receives PREF-02 and forwards the survey answers to reco.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<SubmitPreferenceSurveyResponseDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Submit(
        [FromBody, BindRequired] SubmitPreferenceSurveyRequestDto request,
        CancellationToken ct)
    {
        var result = await _submitPreferenceSurveyUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<SubmitPreferenceSurveyResponseDto>
        {
            Success = true,
            Data = result
        });
    }

    /// <summary>Receives PREF-03 and skips the survey for good.</summary>
    [HttpPost("skip")]
    [ProducesResponseType(typeof(ApiResponse<SkipPreferenceSurveyResponseDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Skip(CancellationToken ct)
    {
        var result = await _skipPreferenceSurveyUseCase.ExecuteAsync(ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<SkipPreferenceSurveyResponseDto>
        {
            Success = true,
            Data = result
        });
    }
}
