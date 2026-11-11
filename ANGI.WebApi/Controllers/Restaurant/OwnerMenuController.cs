using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Restaurant;

/// <summary>Exposes menu management operations for the authenticated restaurant owner.</summary>
[ApiController]
[Route("api/v1/owner/restaurant")]
[Authorize(Roles = "RESTAURANT_OWNER")]
public sealed class OwnerMenuController : ControllerBase
{
    private readonly IGetOwnerMenuUseCase _getMenu;
    private readonly IUpdateDishAvailabilityUseCase _updateAvailability;
    private readonly ICreateMenuSubmissionUseCase _createSubmission;
    private readonly IListMenuSubmissionsUseCase _listSubmissions;
    private readonly IGetMenuSubmissionUseCase _getSubmission;
    private readonly IAddMenuSubmissionItemUseCase _addItem;
    private readonly IUpdateMenuSubmissionItemUseCase _updateItem;
    private readonly IDeleteMenuSubmissionItemUseCase _deleteItem;
    private readonly ISubmitMenuSubmissionUseCase _submitSubmission;
    private readonly ICancelMenuSubmissionUseCase _cancelSubmission;

    /// <summary>Initializes the HTTP boundary with every owner-menu use case.</summary>
    public OwnerMenuController(
        IGetOwnerMenuUseCase getMenu,
        IUpdateDishAvailabilityUseCase updateAvailability,
        ICreateMenuSubmissionUseCase createSubmission,
        IListMenuSubmissionsUseCase listSubmissions,
        IGetMenuSubmissionUseCase getSubmission,
        IAddMenuSubmissionItemUseCase addItem,
        IUpdateMenuSubmissionItemUseCase updateItem,
        IDeleteMenuSubmissionItemUseCase deleteItem,
        ISubmitMenuSubmissionUseCase submitSubmission,
        ICancelMenuSubmissionUseCase cancelSubmission)
    {
        _getMenu = getMenu;
        _updateAvailability = updateAvailability;
        _createSubmission = createSubmission;
        _listSubmissions = listSubmissions;
        _getSubmission = getSubmission;
        _addItem = addItem;
        _updateItem = updateItem;
        _deleteItem = deleteItem;
        _submitSubmission = submitSubmission;
        _cancelSubmission = cancelSubmission;
    }

    /// <summary>Receives OWN-09 and returns the active menu plus an open submission summary.</summary>
    [HttpGet("menu")]
    [ProducesResponseType(typeof(ApiResponse<OwnerMenuDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMenu(CancellationToken ct)
    {
        var result = await _getMenu.ExecuteAsync(ct);
        return Ok(new ApiResponse<OwnerMenuDto> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-10 and toggles direct dish availability.</summary>
    [HttpPut("menu/dishes/{dishId:int}/availability")]
    [ProducesResponseType(typeof(ApiResponse<DishAvailabilityDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAvailability(
        int dishId,
        UpdateDishAvailabilityRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateAvailability.ExecuteAsync(dishId, request, ct);
        return Ok(new ApiResponse<DishAvailabilityDto> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-11 and opens a draft copied from the current menu.</summary>
    [HttpPost("menu-submissions")]
    [ProducesResponseType(typeof(ApiResponse<MenuSubmissionWithItemsDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateSubmission(CancellationToken ct)
    {
        var result = await _createSubmission.ExecuteAsync(ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<MenuSubmissionWithItemsDto> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-12 and returns filtered menu submission history.</summary>
    [HttpGet("menu-submissions")]
    [ProducesResponseType(typeof(ApiResponse<Paged<MenuSubmissionDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSubmissions(
        [FromQuery] ListMenuSubmissionsRequestDto request,
        CancellationToken ct)
    {
        var result = await _listSubmissions.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<Paged<MenuSubmissionDto>> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-13 and returns a submission with its current-menu comparison.</summary>
    [HttpGet("menu-submissions/{submissionId:long}")]
    [ProducesResponseType(typeof(ApiResponse<MenuSubmissionDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubmission(long submissionId, CancellationToken ct)
    {
        var result = await _getSubmission.ExecuteAsync(submissionId, ct);
        return Ok(new ApiResponse<MenuSubmissionDetailDto> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-14 and adds a new proposed dish to a draft.</summary>
    [HttpPost("menu-submissions/{submissionId:long}/items")]
    [ProducesResponseType(typeof(ApiResponse<MenuSubmissionItemDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddItem(
        long submissionId,
        AddMenuSubmissionItemRequestDto request,
        CancellationToken ct)
    {
        var result = await _addItem.ExecuteAsync(submissionId, request, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<MenuSubmissionItemDto> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-15 and partially updates one draft item.</summary>
    [HttpPatch("menu-submissions/{submissionId:long}/items/{itemId:long}")]
    [ProducesResponseType(typeof(ApiResponse<MenuSubmissionItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateItem(
        long submissionId,
        long itemId,
        UpdateMenuSubmissionItemRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateItem.ExecuteAsync(submissionId, itemId, request, ct);
        return Ok(new ApiResponse<MenuSubmissionItemDto> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-16 and removes one item from an editable draft.</summary>
    [HttpDelete("menu-submissions/{submissionId:long}/items/{itemId:long}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteItem(long submissionId, long itemId, CancellationToken ct)
    {
        await _deleteItem.ExecuteAsync(submissionId, itemId, ct);
        return Ok(new ApiResponse<object> { Success = true, Data = null });
    }

    /// <summary>Receives OWN-17 and submits a changed draft for moderator review.</summary>
    [HttpPost("menu-submissions/{submissionId:long}/submit")]
    [ProducesResponseType(typeof(ApiResponse<MenuSubmissionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitSubmission(
        long submissionId,
        SubmitMenuSubmissionRequestDto request,
        CancellationToken ct)
    {
        var result = await _submitSubmission.ExecuteAsync(submissionId, request, ct);
        return Ok(new ApiResponse<MenuSubmissionDto> { Success = true, Data = result });
    }

    /// <summary>Receives OWN-18 and cancels a draft or pending submission.</summary>
    [HttpPost("menu-submissions/{submissionId:long}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<MenuSubmissionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelSubmission(long submissionId, CancellationToken ct)
    {
        var result = await _cancelSubmission.ExecuteAsync(submissionId, ct);
        return Ok(new ApiResponse<MenuSubmissionDto> { Success = true, Data = result });
    }
}
