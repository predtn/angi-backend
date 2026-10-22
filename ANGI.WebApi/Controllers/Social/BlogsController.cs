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
    private readonly IEditBlogUseCase _editBlogUseCase;

    /// <summary>Initializes the HTTP boundary with the BLOG-04 and BLOG-06 use cases.</summary>
    public BlogsController(
        ICreateBlogUseCase createBlogUseCase,
        IEditBlogUseCase editBlogUseCase)
    {
        _createBlogUseCase = createBlogUseCase;
        _editBlogUseCase = editBlogUseCase;
    }

    /// <summary>Receives BLOG-04 and publishes a blog with an optional immutable roadmap snapshot.</summary>
    // BLOG-04
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
}
