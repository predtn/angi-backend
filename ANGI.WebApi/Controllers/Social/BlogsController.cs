using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Social;

/// <summary>Exposes traveler-authored blog operations.</summary>
[ApiController]
[Route("api/v1/blogs")]
[Authorize(Roles = "TRAVELER")]
public sealed class BlogsController : ControllerBase
{
    private readonly ICreateBlogUseCase _createBlogUseCase;
    private readonly ICreateBlogCommentUseCase _createBlogCommentUseCase;
    private readonly IDeleteBlogCommentUseCase _deleteBlogCommentUseCase;

    /// <summary>Initializes the HTTP boundary with the blog and comment use cases.</summary>
    public BlogsController(
        ICreateBlogUseCase createBlogUseCase,
        ICreateBlogCommentUseCase createBlogCommentUseCase,
        IDeleteBlogCommentUseCase deleteBlogCommentUseCase)
    {
        _createBlogUseCase = createBlogUseCase;
        _createBlogCommentUseCase = createBlogCommentUseCase;
        _deleteBlogCommentUseCase = deleteBlogCommentUseCase;
    }

    /// <summary>Initializes the HTTP boundary with only the BLOG-04 use case for backwards compatibility.</summary>
    public BlogsController(ICreateBlogUseCase createBlogUseCase)
        : this(createBlogUseCase, null!, null!)
    {
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

    // BLOG-10
    /// <summary>Receives BLOG-10 and posts a top-level comment to an active blog.</summary>
    [HttpPost("{blogId:long}/comments")]
    [ProducesResponseType(typeof(ApiResponse<CommentDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateComment(
        [FromRoute] long blogId,
        [FromBody] CreateBlogCommentRequestDto request,
        CancellationToken ct)
    {
        var result = await _createBlogCommentUseCase.ExecuteAsync(blogId, request, ct);
        return StatusCode(
            StatusCodes.Status201Created,
            new ApiResponse<CommentDto> { Success = true, Data = result });
    }

    // BLOG-11
    /// <summary>Receives BLOG-11 and soft-deletes an owned blog comment.</summary>
    [HttpDelete("{blogId:long}/comments/{commentId:long}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteComment(
        [FromRoute] long blogId,
        [FromRoute] long commentId,
        CancellationToken ct)
    {
        await _deleteBlogCommentUseCase.ExecuteAsync(blogId, commentId, ct);
        return Ok(new ApiResponse<object?> { Success = true, Data = null });
    }
}
