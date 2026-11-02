using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes blog moderation endpoints (MOD-B1, MOD-B2) to Mod and Admin accounts.</summary>
[ApiController]
[Route("api/v1/mod/blogs")]
[Authorize(Roles = "MODERATOR,ADMIN,MOD")]
public sealed class ModBlogsController(
    IGetModBlogsUseCase listBlogs,
    IUpdateBlogStatusUseCase updateBlogStatus) : ControllerBase
{
    /// <summary>MOD-B1: Lấy danh sách blog dành cho Mod kiểm duyệt.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<ModBlogCardDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] GetModBlogsRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<Paged<ModBlogCardDto>> { Success = true, Data = await listBlogs.ExecuteAsync(request, ct) });

    /// <summary>MOD-B2: Mod thay đổi trạng thái của blog (VD: Ẩn, Xóa, Khôi phục).</summary>
    [HttpPut("{blogId:long}/status")]
    [ProducesResponseType(typeof(ApiResponse<UpdateBlogStatusResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(long blogId, UpdateBlogStatusRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<UpdateBlogStatusResponseDto> { Success = true, Data = await updateBlogStatus.ExecuteAsync(blogId, request, ct) });
}
