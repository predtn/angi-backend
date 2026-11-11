using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Administration;

/// <summary>Exposes MOD account listing, creation, detail, and update operations.</summary>
[Authorize(Roles = "ADMIN")]
[ApiController]
[Route("api/v1/admin/mods")]
public sealed class ModsController : ControllerBase
{
    private readonly IListModsUseCase _listModsUseCase;
    private readonly ICreateModUseCase _createModUseCase;
    private readonly IGetModDetailUseCase _getModDetailUseCase;
    private readonly IUpdateModUseCase _updateModUseCase;

    /// <summary>Initializes the controller with one use case for each MOD account operation.</summary>
    public ModsController(
        IListModsUseCase listModsUseCase,
        ICreateModUseCase createModUseCase,
        IGetModDetailUseCase getModDetailUseCase,
        IUpdateModUseCase updateModUseCase)
    {
        _listModsUseCase = listModsUseCase;
        _createModUseCase = createModUseCase;
        _getModDetailUseCase = getModDetailUseCase;
        _updateModUseCase = updateModUseCase;
    }

    // ADM-M1
    /// <summary>Returns a filtered page of MOD accounts.</summary>
    /// <param name="request">Contains optional search and status filters plus pagination.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The MOD account page was returned.</response>
    /// <response code="400">The query values failed validation.</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<AdminUserCardDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] ListModsRequestDto request, CancellationToken ct)
    {
        var result = await _listModsUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<Paged<AdminUserCardDto>> { Success = true, Data = result });
    }

    // ADM-M2
    /// <summary>Creates an active MOD account and sends its password setup invitation.</summary>
    /// <param name="request">Contains identity fields and optional permission overrides.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="201">The MOD account was created.</response>
    /// <response code="400">Validation failed or a permission code is unknown.</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="409">The email is already used by a non-deleted account.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AdminUserCardDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateModRequestDto request, CancellationToken ct)
    {
        var result = await _createModUseCase.ExecuteAsync(request, ct);
        return StatusCode(
            StatusCodes.Status201Created,
            new ApiResponse<AdminUserCardDto> { Success = true, Data = result });
    }

    // ADM-M3
    /// <summary>Returns one MOD account with permissions and all-time handling counts.</summary>
    /// <param name="userId">Identifies the MOD account.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The MOD details were returned.</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="404">The MOD account does not exist.</response>
    [HttpGet("{userId:int}")]
    [ProducesResponseType(typeof(ApiResponse<ModDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int userId, CancellationToken ct)
    {
        var result = await _getModDetailUseCase.ExecuteAsync(userId, ct);
        return Ok(new ApiResponse<ModDetailDto> { Success = true, Data = result });
    }

    // ADM-M4
    /// <summary>Updates a MOD display name or activation state and revokes sessions on deactivation.</summary>
    /// <param name="userId">Identifies the MOD account.</param>
    /// <param name="request">Contains one or both mutable fields.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The MOD account was updated.</response>
    /// <response code="400">The patch failed validation.</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="404">The MOD account does not exist.</response>
    [HttpPatch("{userId:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminUserCardDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        int userId,
        [FromBody] UpdateModRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateModUseCase.ExecuteAsync(userId, request, ct);
        return Ok(new ApiResponse<AdminUserCardDto> { Success = true, Data = result });
    }
}
