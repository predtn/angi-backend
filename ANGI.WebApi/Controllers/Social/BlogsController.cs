using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Social;

/// <summary>Exposes public and traveler-authored blog operations (BLOG-01, BLOG-02, BLOG-03, BLOG-04, BLOG-06 to BLOG-10).</summary>
[ApiController]
[Route("api/v1/blogs")]
[Authorize(Roles = "TRAVELER")]
public sealed class BlogsController : ControllerBase
{
    private readonly IGetPublicBlogsUseCase _getPublicBlogsUseCase;
    private readonly ICreateBlogUseCase _createBlogUseCase;
    private readonly IGetBlogDetailUseCase _getBlogDetailUseCase;
    private readonly IGetBlogCommentsUseCase _getBlogCommentsUseCase;
    private readonly IEditBlogUseCase _editBlogUseCase;
    private readonly IDeleteBlogUseCase _deleteBlogUseCase;
    private readonly ILikeBlogUseCase _likeBlogUseCase;
    private readonly IUnlikeBlogUseCase _unlikeBlogUseCase;
    private readonly ICreateBlogCommentUseCase _createBlogCommentUseCase;

    /// <summary>Initializes the HTTP boundary with Social blog use cases.</summary>
    public BlogsController(
        IGetPublicBlogsUseCase getPublicBlogsUseCase,
        ICreateBlogUseCase createBlogUseCase,
        IGetBlogDetailUseCase getBlogDetailUseCase,
        IGetBlogCommentsUseCase getBlogCommentsUseCase,
        IEditBlogUseCase editBlogUseCase,
        IDeleteBlogUseCase deleteBlogUseCase,
        ILikeBlogUseCase likeBlogUseCase,
        IUnlikeBlogUseCase unlikeBlogUseCase,
        ICreateBlogCommentUseCase createBlogCommentUseCase)
    {
        _getPublicBlogsUseCase = getPublicBlogsUseCase;
        _createBlogUseCase = createBlogUseCase;
        _getBlogDetailUseCase = getBlogDetailUseCase;
        _getBlogCommentsUseCase = getBlogCommentsUseCase;
        _editBlogUseCase = editBlogUseCase;
        _deleteBlogUseCase = deleteBlogUseCase;
        _likeBlogUseCase = likeBlogUseCase;
        _unlikeBlogUseCase = unlikeBlogUseCase;
        _createBlogCommentUseCase = createBlogCommentUseCase;
    }

    // BLOG-01
    /// <summary>Receives BLOG-01 and returns a paginated list of public published blogs.</summary>
    /// <param name="request">Query parameters for filtering, sorting, and pagination.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    /// <response code="200">Returns the paginated list of public blog cards.</response>
    /// <response code="400">Query parameters failed validation (VALIDATION_FAILED).</response>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<Paged<BlogCardDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPublicBlogs(
        [FromQuery] GetPublicBlogsRequestDto request,
        CancellationToken ct)
    {
        var result = await _getPublicBlogsUseCase.ExecuteAsync(request, ct);
        return StatusCode(
            StatusCodes.Status200OK,
            new ApiResponse<Paged<BlogCardDto>> { Success = true, Data = result });
    }

    // BLOG-04
    /// <summary>Receives BLOG-04 and publishes a blog with an optional immutable roadmap snapshot.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<BlogDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(CreateBlogRequestDto request, CancellationToken ct)
    {
        var result = await _createBlogUseCase.ExecuteAsync(request, ct);
        return StatusCode(
            StatusCodes.Status201Created,
            new ApiResponse<BlogDetailDto> { Success = true, Data = result });
    }

    // BLOG-02
    /// <summary>Receives BLOG-02 and returns the full detail of a published blog, author, and roadmap snapshot.</summary>
    /// <param name="blogId">ID của bài viết blog.</param>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="200">Lấy chi tiết bài viết thành công.</response>
    /// <response code="400">Định dạng tham số không hợp lệ (VALIDATION_FAILED).</response>
    /// <response code="404">Không tìm thấy bài viết (BLOG_NOT_FOUND).</response>
    [AllowAnonymous]
    [HttpGet("{blogId}")]
    [ProducesResponseType(typeof(ApiResponse<BlogDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail([FromRoute] long blogId, CancellationToken ct)
    {
        var result = await _getBlogDetailUseCase.ExecuteAsync(blogId, ct);
        return Ok(new ApiResponse<BlogDetailDto> { Success = true, Data = result });
    }

    // BLOG-03
    /// <summary>Receives BLOG-03 and returns a paginated list of visible comments for a published blog.</summary>
    /// <param name="blogId">ID của bài viết blog.</param>
    /// <param name="request">Tham số phân trang (page, pageSize).</param>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="200">Lấy danh sách bình luận thành công.</response>
    /// <response code="400">Tham số phân trang hoặc ID không hợp lệ (VALIDATION_FAILED).</response>
    /// <response code="404">Không tìm thấy bài viết (BLOG_NOT_FOUND).</response>
    [AllowAnonymous]
    [HttpGet("{blogId}/comments")]
    [ProducesResponseType(typeof(ApiResponse<Paged<CommentDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComments(
        [FromRoute] long blogId,
        [FromQuery] GetBlogCommentsRequestDto request,
        CancellationToken ct)
    {
        var result = await _getBlogCommentsUseCase.ExecuteAsync(blogId, request, ct);
        return Ok(new ApiResponse<Paged<CommentDto>> { Success = true, Data = result });
    }

    /// <summary>Receives BLOG-06 and edits a traveler-owned blog.</summary>
    // BLOG-06
    [HttpPatch("{blogId:long}")]
    [ProducesResponseType(typeof(ApiResponse<BlogDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Edit(
        [FromRoute] long blogId,
        [FromBody] EditBlogRequestDto request,
        CancellationToken ct)
    {
        var result = await _editBlogUseCase.ExecuteAsync(blogId, request, ct);
        return Ok(new ApiResponse<BlogDetailDto> { Success = true, Data = result });
    }

    /// <summary>Receives BLOG-07 and soft-deletes a traveler-owned blog.</summary>
    [HttpDelete("{blogId:long}")]
    [Authorize(Roles = "TRAVELER,Traveler")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long blogId, CancellationToken ct)
    {
        await _deleteBlogUseCase.ExecuteAsync(blogId, ct);
        return Ok(new ApiResponse<object> { Success = true, Data = null });
    }

    // BLOG-08
    /// <summary>Receives BLOG-08 and idempotently likes a blog.</summary>
    [HttpPut("{blogId:long}/like")]
    [ProducesResponseType(typeof(ApiResponse<BlogLikeResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Like([FromRoute] long blogId, CancellationToken ct)
    {
        var result = await _likeBlogUseCase.ExecuteAsync(blogId, ct);
        return Ok(new ApiResponse<BlogLikeResponseDto> { Success = true, Data = result });
    }

    // BLOG-09
    /// <summary>Receives BLOG-09 and idempotently unlikes a blog.</summary>
    [HttpDelete("{blogId:long}/like")]
    [ProducesResponseType(typeof(ApiResponse<BlogLikeResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlike([FromRoute] long blogId, CancellationToken ct)
    {
        var result = await _unlikeBlogUseCase.ExecuteAsync(blogId, ct);
        return Ok(new ApiResponse<BlogLikeResponseDto> { Success = true, Data = result });
    }

    // BLOG-10
    /// <summary>Receives BLOG-10 and posts a comment on a blog.</summary>
    [HttpPost("{blogId:long}/comments")]
    [ProducesResponseType(typeof(ApiResponse<CommentDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Comment(
        [FromRoute] long blogId,
        [FromBody, BindRequired] CreateBlogCommentRequestDto request,
        CancellationToken ct)
    {
        var result = await _createBlogCommentUseCase.ExecuteAsync(blogId, request, ct);
        return StatusCode(
            StatusCodes.Status201Created,
            new ApiResponse<CommentDto> { Success = true, Data = result });
    }
}
