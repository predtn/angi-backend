using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Administration;

/// <summary>Exposes administrative dashboard metrics, time-series, and top statistics.</summary>
[Authorize(Roles = "ADMIN")]
[ApiController]
[Route("api/v1/admin/dashboard")]
public sealed class AdminDashboardController : ControllerBase
{
    private readonly IGetDashboardSummaryUseCase _getDashboardSummaryUseCase;
    private readonly IGetDashboardTimeSeriesUseCase _getDashboardTimeSeriesUseCase;
    private readonly IGetDashboardTopUseCase _getDashboardTopUseCase;

    /// <summary>Initializes the controller with use cases for dashboard operations.</summary>
    public AdminDashboardController(
        IGetDashboardSummaryUseCase getDashboardSummaryUseCase,
        IGetDashboardTimeSeriesUseCase getDashboardTimeSeriesUseCase,
        IGetDashboardTopUseCase getDashboardTopUseCase)
    {
        _getDashboardSummaryUseCase = getDashboardSummaryUseCase;
        _getDashboardTimeSeriesUseCase = getDashboardTimeSeriesUseCase;
        _getDashboardTopUseCase = getDashboardTopUseCase;
    }

    // ADM-D1
    /// <summary>Retrieves aggregated dashboard summary metrics within a date range.</summary>
    /// <param name="request">Contains optional from and to date boundaries.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The dashboard summary metrics were successfully returned.</response>
    /// <response code="400">The query parameters failed validation (e.g. from > to or range > 366 days).</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="500">An unexpected server error occurred.</response>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ApiResponse<DashboardSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetSummary([FromQuery] GetDashboardSummaryRequestDto request, CancellationToken ct)
    {
        var result = await _getDashboardSummaryUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<DashboardSummaryDto> { Success = true, Data = result });
    }

    // ADM-D2
    /// <summary>Retrieves time-series data for chart visualization grouped by day, week, or month.</summary>
    /// <param name="request">Contains the metric name, from and to date range, and optional interval.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The time-series chart data was successfully returned.</response>
    /// <response code="400">The query parameters failed validation.</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="500">An unexpected server error occurred.</response>
    [HttpGet("timeseries")]
    [ProducesResponseType(typeof(ApiResponse<DashboardTimeSeriesDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTimeSeries([FromQuery] GetDashboardTimeSeriesRequestDto request, CancellationToken ct)
    {
        var result = await _getDashboardTimeSeriesUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<DashboardTimeSeriesDto> { Success = true, Data = result });
    }

    // ADM-D3
    /// <summary>Retrieves top ranking items (dishes, restaurants, or blogs).</summary>
    /// <param name="request">Contains the ranking category type and result limit.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The top ranking items were successfully returned.</response>
    /// <response code="400">The query parameters failed validation.</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="500">An unexpected server error occurred.</response>
    [HttpGet("top")]
    [ProducesResponseType(typeof(ApiResponse<DashboardTopDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTop([FromQuery] GetDashboardTopRequestDto request, CancellationToken ct)
    {
        var result = await _getDashboardTopUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<DashboardTopDto> { Success = true, Data = result });
    }
}
