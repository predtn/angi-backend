using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Social;

/// <summary>Exposes content reporting operations for travelers.</summary>
[ApiController]
[Authorize(Roles = "TRAVELER")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportBlogUseCase _reportBlogUseCase;

    /// <summary>Initializes the HTTP boundary with the REP-02 use case.</summary>
    public ReportsController(IReportBlogUseCase reportBlogUseCase)
    {
        _reportBlogUseCase = reportBlogUseCase;
    }

    // REP-02
    /// <summary>Receives REP-02 and reports a blog for moderation.</summary>
    [HttpPost("api/v1/blogs/{blogId:long}/reports")]
    [ProducesResponseType(typeof(ApiResponse<CreateReportResponseDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReportBlog(
        [FromRoute] long blogId,
        [FromBody, BindRequired] ReportBlogRequestDto request,
        CancellationToken ct)
    {
        var result = await _reportBlogUseCase.ExecuteAsync(blogId, request, ct);
        return StatusCode(
            StatusCodes.Status201Created,
            new ApiResponse<CreateReportResponseDto> { Success = true, Data = result });
    }
}
