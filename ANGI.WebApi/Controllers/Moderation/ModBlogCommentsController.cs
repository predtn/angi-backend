using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes blog comment moderation endpoint (MOD-B3) to Mod and Admin accounts.</summary>
[ApiController]
[Route("api/v1/mod/blog-comments")]
[Authorize(Roles = "MODERATOR,ADMIN,MOD")]
public sealed class ModBlogCommentsController(IUpdateBlogCommentStatusUseCase updateCommentStatus) : ControllerBase
{
    /// <summary>MOD-B3: Mod thay đổi trạng thái của bình luận trong blog (VD: Ẩn, Xóa, Khôi phục).</summary>
    [HttpPut("{commentId:long}/status")]
    [ProducesResponseType(typeof(ApiResponse<UpdateBlogCommentStatusResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(long commentId, UpdateBlogCommentStatusRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<UpdateBlogCommentStatusResponseDto> { Success = true, Data = await updateCommentStatus.ExecuteAsync(commentId, request, ct) });
}
