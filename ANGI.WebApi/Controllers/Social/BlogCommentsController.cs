using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Social;

/// <summary>Exposes BLOG-11; BLOG-10 is served by BlogsController.</summary>
[ApiController]
[Route("api/v1/blogs/{blogId:long}/comments")]
[Authorize(Roles = "TRAVELER")]
public sealed class BlogCommentsController : ControllerBase
{
    private readonly IDeleteBlogCommentUseCase _deleteBlogCommentUseCase;

    /// <summary>Initializes the HTTP boundary with the BLOG-11 use case.</summary>
    public BlogCommentsController(IDeleteBlogCommentUseCase deleteBlogCommentUseCase)
    {
        _deleteBlogCommentUseCase = deleteBlogCommentUseCase;
    }

    // BLOG-11
    /// <summary>Receives BLOG-11 and soft-deletes an owned blog comment.</summary>
    [HttpDelete("{commentId:long}")]
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
