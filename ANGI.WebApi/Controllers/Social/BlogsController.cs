using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Social;

/// <summary>Exposes public and traveler-authored blog operations.</summary>
[ApiController]
[Route("api/v1/blogs")]
[Authorize(Roles = "TRAVELER")]
public sealed class BlogsController : ControllerBase
{
    private readonly IGetPublicBlogsUseCase _getPublicBlogsUseCase;
    private readonly ICreateBlogUseCase _createBlogUseCase;

    /// <summary>Initializes the HTTP boundary with Social blog use cases.</summary>
    public BlogsController(
        IGetPublicBlogsUseCase getPublicBlogsUseCase,
        ICreateBlogUseCase createBlogUseCase)
    {
        _getPublicBlogsUseCase = getPublicBlogsUseCase;
        _createBlogUseCase = createBlogUseCase;
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
}
