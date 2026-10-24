using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes atomic report conclusions to authorized Mod and Admin accounts.</summary>
[ApiController]
[Route("api/v1/mod/reports")]
[Authorize(Roles = "MOD,ADMIN")]
public sealed class ModReportsController(IResolveReportUseCase resolve) : ControllerBase
{
    /// <summary>MOD-RP4: resolves or dismisses a report and applies the associated moderation action.</summary>
    [HttpPost("{reportId:long}/resolve")]
    public async Task<IActionResult> Resolve(long reportId, ResolveReportRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<ReportDto> { Success = true, Data = await resolve.ExecuteAsync(reportId, request, ct) });
}
