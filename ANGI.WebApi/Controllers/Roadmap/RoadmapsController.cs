using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Roadmap;

/// <summary>Exposes personal roadmap planning operations for authenticated travelers.</summary>
[ApiController]
[Route("api/v1/roadmaps")]
[Authorize(Roles = "TRAVELER")]
public sealed class RoadmapsController : ControllerBase
{
    private readonly ICreateRoadmapUseCase _createRoadmapUseCase;
    private readonly IGetMyRoadmapsUseCase _getMyRoadmapsUseCase;
    private readonly IUpdateRoadmapUseCase _updateRoadmapUseCase;

    /// <summary>Initializes the HTTP boundary with roadmap planning use cases.</summary>
    public RoadmapsController(
        ICreateRoadmapUseCase createRoadmapUseCase,
        IGetMyRoadmapsUseCase getMyRoadmapsUseCase,
        IUpdateRoadmapUseCase updateRoadmapUseCase)
    {
        _createRoadmapUseCase = createRoadmapUseCase;
        _getMyRoadmapsUseCase = getMyRoadmapsUseCase;
        _updateRoadmapUseCase = updateRoadmapUseCase;
    }

    /// <summary>Receives RM-02 and returns one page of the current traveler's roadmap cards.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<RoadmapSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMyRoadmaps(
        [FromQuery] GetMyRoadmapsRequestDto request,
        CancellationToken ct)
    {
        var result = await _getMyRoadmapsUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<Paged<RoadmapSummaryDto>> { Success = true, Data = result });
    }

    /// <summary>Receives RM-01 and creates a roadmap with its initially empty days.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<RoadmapDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(CreateRoadmapRequestDto request, CancellationToken ct)
    {
        var result = await _createRoadmapUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<RoadmapDetailDto>
        {
            Success = true,
            Data = result
        });
    }

    /// <summary>Receives RM-04 and partially updates a traveler-owned roadmap.</summary>
    [HttpPatch("{roadmapId:long}")]
    [ProducesResponseType(typeof(ApiResponse<RoadmapDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        long roadmapId,
        UpdateRoadmapRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateRoadmapUseCase.ExecuteAsync(roadmapId, request, ct);
        return Ok(new ApiResponse<RoadmapDetailDto> { Success = true, Data = result });
    }
}
