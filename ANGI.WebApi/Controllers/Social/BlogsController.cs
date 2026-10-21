using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Social;

/// <summary>Exposes public and traveler blog operations.</summary>
[ApiController]
[Route("api/v1/blogs")]
[Authorize(Roles = "TRAVELER")]
public sealed class BlogsController : ControllerBase
{
    private readonly ICreateBlogUseCase _createBlogUseCase;

    public BlogsController(ICreateBlogUseCase createBlogUseCase)
    {
        _createBlogUseCase = createBlogUseCase;
    }

    // BLOG-04
    /// <summary>Viết blog hoặc chia sẻ lộ trình (BLOG-04).</summary>
    /// <param name="request">Dữ liệu tạo blog mới.</param>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="201">Tạo bài viết thành công.</response>
    /// <response code="400">Dữ liệu không hợp lệ (VALIDATION_FAILED).</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ.</response>
    /// <response code="403">Không có quyền thực hiện hành động này (chỉ dành cho Traveler).</response>
    /// <response code="404">Không tìm thấy lộ trình (ROADMAP_NOT_FOUND).</response>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<BlogDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(CreateBlogRequestDto request, CancellationToken ct)
    {
        var result = await _createBlogUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<BlogDetailDto>
        {
            Success = true,
            Data = result
        });
    }
}
