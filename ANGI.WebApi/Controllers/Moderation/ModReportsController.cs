using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.Common.Models;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes report queues, details, claims and atomic conclusions to Mod and Admin accounts.</summary>
[ApiController]
[Route("api/v1/mod/reports")]
[Authorize(Roles = "MOD,ADMIN")]
public sealed class ModReportsController(IResolveReportUseCase resolve, IListReportsUseCase list,
    IGetReportUseCase get, IClaimReportUseCase claim) : ControllerBase
{
    /// <summary>MOD-RP1: lists reports matching the authorized target type and optional filters.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListReportsRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<Paged<ReportDto>> { Success = true, Data = await list.ExecuteAsync(request, ct) });

    /// <summary>MOD-RP2: returns report evidence, its target and other open reports on that target.</summary>
    [HttpGet("{reportId:long}")]
    public async Task<IActionResult> Get(long reportId, CancellationToken ct) =>
        Ok(new ApiResponse<ReportDetailDto> { Success = true, Data = await get.ExecuteAsync(reportId, ct) });

    /// <summary>MOD-RP3: exclusively assigns a pending report to the authorized caller.</summary>
    [HttpPost("{reportId:long}/claim")]
    public async Task<IActionResult> Claim(long reportId, CancellationToken ct) =>
        Ok(new ApiResponse<ReportDto> { Success = true, Data = await claim.ExecuteAsync(reportId, ct) });

    /// <summary>MOD-RP4: resolves or dismisses a report and applies the associated moderation action.</summary>
    [HttpPost("{reportId:long}/resolve")]
    public async Task<IActionResult> Resolve(long reportId, ResolveReportRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<ReportDto> { Success = true, Data = await resolve.ExecuteAsync(reportId, request, ct) });
}
