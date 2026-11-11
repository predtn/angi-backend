using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Discovery;

/// <summary>Exposes the public tag catalogue (CAT-02).</summary>
[ApiController]
[Route("api/v1/tags")]
public sealed class TagsController : ControllerBase
{
    private readonly IGetTagsUseCase _getTagsUseCase;

    /// <summary>Initializes the HTTP boundary with the tags use case.</summary>
    public TagsController(IGetTagsUseCase getTagsUseCase)
    {
        _getTagsUseCase = getTagsUseCase;
    }

    /// <summary>Receives CAT-02 and returns every tag, or those of one type.</summary>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TagDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get([FromQuery] GetTagsRequestDto request, CancellationToken ct)
    {
        var result = await _getTagsUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<IReadOnlyList<TagDto>> { Success = true, Data = result });
    }
}
