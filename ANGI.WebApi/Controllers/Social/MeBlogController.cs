using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Social;

/// <summary>Exposes personal blog management operations for authenticated travelers (BLOG-05).</summary>
[ApiController]
[Route("api/v1/me/blogs")]
[Authorize(Roles = "TRAVELER")]
public sealed class MeBlogController : ControllerBase
{
    private readonly IGetMyBlogsUseCase _getMyBlogsUseCase;

    /// <summary>Initializes the controller with the BLOG-05 use case.</summary>
    public MeBlogController(IGetMyBlogsUseCase getMyBlogsUseCase)
    {
        _getMyBlogsUseCase = getMyBlogsUseCase;
    }

    // BLOG-05
    /// <summary>Xem danh sách blog do tôi viết, kể cả bị Mod ẩn (BLOG-05).</summary>
    /// <param name="request">Tham số phân trang và bộ lọc trạng thái blog.</param>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="200">Lấy danh sách blog thành công.</response>
    /// <response code="400">Tham số phân trang hoặc bộ lọc không hợp lệ (VALIDATION_FAILED).</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ (UNAUTHORIZED).</response>
    /// <response code="403">Không có quyền truy cập, yêu cầu vai trò TRAVELER (FORBIDDEN).</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<MyBlogCardDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMyBlogs(
        [FromQuery] GetMyBlogsRequestDto request,
        CancellationToken ct)
    {
        var result = await _getMyBlogsUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<Paged<MyBlogCardDto>>
        {
            Success = true,
            Data = result
        });
    }
}
