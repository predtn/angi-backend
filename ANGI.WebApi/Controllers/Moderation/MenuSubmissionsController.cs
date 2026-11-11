using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes the menu review queue and decisions to authorized moderators and administrators.</summary>
[ApiController]
[Route("api/v1/mod/menu-submissions")]
[Authorize(Roles = "MOD,ADMIN")]
public sealed class MenuSubmissionsController(
    IListModMenuSubmissionsUseCase list, IGetMenuSubmissionReviewUseCase detail,
    IApproveMenuSubmissionUseCase approve, IRejectMenuSubmissionUseCase reject) : ControllerBase
{
    /// <summary>MOD-M1: Returns a filtered page of menu submissions and change counts.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListModMenuSubmissionsRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<Paged<ModMenuSubmissionCardDto>> { Success = true, Data = await list.ExecuteAsync(request, ct) });

    /// <summary>MOD-M2: Returns proposed menu items alongside their current values.</summary>
    [HttpGet("{submissionId:long}")]
    public async Task<IActionResult> Detail(long submissionId, CancellationToken ct) =>
        Ok(new ApiResponse<ModMenuSubmissionDetailDto> { Success = true, Data = await detail.ExecuteAsync(submissionId, ct) });

    /// <summary>MOD-M3: Approves a pending submission and returns affected dish identifiers.</summary>
    [HttpPost("{submissionId:long}/approve")]
    public async Task<IActionResult> Approve(long submissionId, ApproveMenuSubmissionRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<ApproveMenuSubmissionResponseDto> { Success = true, Data = await approve.ExecuteAsync(submissionId, request, ct) });

    /// <summary>MOD-M4: Rejects a pending submission with a required reason for its owner.</summary>
    [HttpPost("{submissionId:long}/reject")]
    public async Task<IActionResult> Reject(long submissionId, RejectMenuSubmissionRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<MenuSubmissionDto> { Success = true, Data = await reject.ExecuteAsync(submissionId, request, ct) });
}
