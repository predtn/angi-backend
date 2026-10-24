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
    private readonly IGetRoadmapDetailUseCase _getRoadmapDetailUseCase;
    private readonly IGetRoadmapItemDetailUseCase _getRoadmapItemDetailUseCase;
    private readonly IGetRoadmapDayRouteUseCase _getRoadmapDayRouteUseCase;
    private readonly IGetRoadmapSuggestionsUseCase _getRoadmapSuggestionsUseCase;
    private readonly IAddRoadmapItemUseCase _addRoadmapItemUseCase;
    private readonly IUpdateRoadmapItemUseCase _updateRoadmapItemUseCase;
    private readonly IUpdateRoadmapUseCase _updateRoadmapUseCase;
    private readonly IDeleteRoadmapUseCase _deleteRoadmapUseCase;
    private readonly IDeleteRoadmapItemUseCase _deleteRoadmapItemUseCase;
    private readonly IRateRoadmapItemUseCase _rateRoadmapItemUseCase;

    /// <summary>Initializes the HTTP boundary with roadmap planning use cases.</summary>
    public RoadmapsController(
        ICreateRoadmapUseCase createRoadmapUseCase,
        IGetMyRoadmapsUseCase getMyRoadmapsUseCase,
        IGetRoadmapDetailUseCase getRoadmapDetailUseCase,
        IGetRoadmapItemDetailUseCase getRoadmapItemDetailUseCase,
        IGetRoadmapDayRouteUseCase getRoadmapDayRouteUseCase,
        IGetRoadmapSuggestionsUseCase getRoadmapSuggestionsUseCase,
        IAddRoadmapItemUseCase addRoadmapItemUseCase,
        IUpdateRoadmapItemUseCase updateRoadmapItemUseCase,
        IUpdateRoadmapUseCase updateRoadmapUseCase,
        IDeleteRoadmapUseCase deleteRoadmapUseCase,
        IDeleteRoadmapItemUseCase deleteRoadmapItemUseCase,
        IRateRoadmapItemUseCase rateRoadmapItemUseCase)
    {
        _createRoadmapUseCase = createRoadmapUseCase;
        _getMyRoadmapsUseCase = getMyRoadmapsUseCase;
        _getRoadmapDetailUseCase = getRoadmapDetailUseCase;
        _getRoadmapItemDetailUseCase = getRoadmapItemDetailUseCase;
        _getRoadmapDayRouteUseCase = getRoadmapDayRouteUseCase;
        _getRoadmapSuggestionsUseCase = getRoadmapSuggestionsUseCase;
        _addRoadmapItemUseCase = addRoadmapItemUseCase;
        _updateRoadmapItemUseCase = updateRoadmapItemUseCase;
        _updateRoadmapUseCase = updateRoadmapUseCase;
        _deleteRoadmapUseCase = deleteRoadmapUseCase;
        _deleteRoadmapItemUseCase = deleteRoadmapItemUseCase;
        _rateRoadmapItemUseCase = rateRoadmapItemUseCase;
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

    /// <summary>Receives RM-03 and returns Overview, Itinerary, and Budget for an owned roadmap.</summary>
    [HttpGet("{roadmapId:long}")]
    [ProducesResponseType(typeof(ApiResponse<RoadmapDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(long roadmapId, CancellationToken ct)
    {
        var result = await _getRoadmapDetailUseCase.ExecuteAsync(roadmapId, ct);
        return Ok(new ApiResponse<RoadmapDetailDto> { Success = true, Data = result });
    }

    /// <summary>Receives RM-12 and returns one owned item's complete dish, restaurant, feedback, and review detail.</summary>
    [HttpGet("{roadmapId:long}/items/{itemId:long}")]
    [ProducesResponseType(typeof(ApiResponse<RoadmapItemDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetItemDetail(long roadmapId, long itemId, CancellationToken ct)
    {
        var result = await _getRoadmapItemDetailUseCase.ExecuteAsync(roadmapId, itemId, ct);
        return Ok(new ApiResponse<RoadmapItemDetailDto> { Success = true, Data = result });
    }

    /// <summary>Receives RM-06 and returns one ranked page of dishes available to add to the roadmap.</summary>
    [HttpGet("{roadmapId:long}/suggestions")]
    [ProducesResponseType(typeof(ApiResponse<GetRoadmapSuggestionsResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSuggestions(
        long roadmapId,
        [FromQuery] GetRoadmapSuggestionsRequestDto request,
        CancellationToken ct)
    {
        var result = await _getRoadmapSuggestionsUseCase.ExecuteAsync(roadmapId, request, ct);
        return Ok(new ApiResponse<GetRoadmapSuggestionsResponseDto> { Success = true, Data = result });
    }

    /// <summary>Receives RM-07 and manually adds one available dish to an owned roadmap day.</summary>
    [HttpPost("{roadmapId:long}/days/{dayId:long}/items")]
    [ProducesResponseType(typeof(ApiResponse<RoadmapItemDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddItem(
        long roadmapId,
        long dayId,
        AddRoadmapItemRequestDto request,
        CancellationToken ct)
    {
        var result = await _addRoadmapItemUseCase.ExecuteAsync(roadmapId, dayId, request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<RoadmapItemDto>
        {
            Success = true,
            Data = result
        });
    }

    /// <summary>Receives RM-08 and replaces or repositions one item in an owned roadmap.</summary>
    [HttpPatch("{roadmapId:long}/items/{itemId:long}")]
    [ProducesResponseType(typeof(ApiResponse<RoadmapItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateItem(
        long roadmapId,
        long itemId,
        UpdateRoadmapItemRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateRoadmapItemUseCase.ExecuteAsync(roadmapId, itemId, request, ct);
        return Ok(new ApiResponse<RoadmapItemDto> { Success = true, Data = result });
    }

    /// <summary>Receives RM-14 and returns travel legs for one day of an owned roadmap.</summary>
    [HttpGet("{roadmapId:long}/days/{dayId:long}/route")]
    [ProducesResponseType(typeof(ApiResponse<GetRoadmapDayRouteResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDayRoute(
        long roadmapId,
        long dayId,
        [FromQuery] GetRoadmapDayRouteRequestDto request,
        CancellationToken ct)
    {
        var result = await _getRoadmapDayRouteUseCase.ExecuteAsync(roadmapId, dayId, request, ct);
        return Ok(new ApiResponse<GetRoadmapDayRouteResponseDto> { Success = true, Data = result });
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

    /// <summary>Receives RM-05 and soft-deletes a traveler-owned roadmap.</summary>
    [HttpDelete("{roadmapId:long}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long roadmapId, CancellationToken ct)
    {
        await _deleteRoadmapUseCase.ExecuteAsync(roadmapId, ct);
        return Ok(new ApiResponse<object> { Success = true, Data = null });
    }

    /// <summary>Receives RM-09 and permanently removes one item from a traveler-owned roadmap.</summary>
    [HttpDelete("{roadmapId:long}/items/{itemId:long}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteItem(long roadmapId, long itemId, CancellationToken ct)
    {
        await _deleteRoadmapItemUseCase.ExecuteAsync(roadmapId, itemId, ct);
        return Ok(new ApiResponse<object> { Success = true, Data = null });
    }

    /// <summary>Receives RM-13 and records the traveler's one like or dislike for a roadmap item's dish.</summary>
    [HttpPost("{roadmapId:long}/items/{itemId:long}/feedback")]
    [ProducesResponseType(typeof(ApiResponse<RateRoadmapItemResponseDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RateItem(
        long roadmapId,
        long itemId,
        RateRoadmapItemRequestDto request,
        CancellationToken ct)
    {
        var result = await _rateRoadmapItemUseCase.ExecuteAsync(roadmapId, itemId, request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<RateRoadmapItemResponseDto>
        {
            Success = true,
            Data = result
        });
    }
}
