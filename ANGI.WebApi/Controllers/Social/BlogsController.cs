using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Social;

/// <summary>Exposes traveler-authored blog operations.</summary>
[ApiController]
[Route("api/v1/blogs")]
[Authorize(Roles = "TRAVELER")]
public sealed class BlogsController : ControllerBase
{
    private readonly ICreateBlogUseCase _createBlogUseCase;
    private readonly ILikeBlogUseCase _likeBlogUseCase;
    private readonly IUnlikeBlogUseCase _unlikeBlogUseCase;
    private readonly ICreateBlogCommentUseCase _createBlogCommentUseCase;

    /// <summary>Initializes the HTTP boundary with the blog use cases.</summary>
    public BlogsController(
        ICreateBlogUseCase createBlogUseCase,
        ILikeBlogUseCase likeBlogUseCase,
        IUnlikeBlogUseCase unlikeBlogUseCase,
        ICreateBlogCommentUseCase createBlogCommentUseCase)
    {
        _createBlogUseCase = createBlogUseCase;
        _likeBlogUseCase = likeBlogUseCase;
        _unlikeBlogUseCase = unlikeBlogUseCase;
        _createBlogCommentUseCase = createBlogCommentUseCase;
    }

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
