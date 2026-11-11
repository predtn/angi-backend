using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Administration;

/// <summary>Exposes permission catalog, MOD override, and MOD role-default administration endpoints.</summary>
[Authorize(Roles = "ADMIN")]
[ApiController]
[Route("api/v1/admin")]
public sealed class PermissionsController : ControllerBase
{
    private readonly IGetPermissionCatalogUseCase _getPermissionCatalogUseCase;
    private readonly IGetModPermissionsUseCase _getModPermissionsUseCase;
    private readonly IReplaceModPermissionsUseCase _replaceModPermissionsUseCase;
    private readonly IReplaceModRolePermissionsUseCase _replaceModRolePermissionsUseCase;

    /// <summary>Initializes the controller with one use case for each permission administration operation.</summary>
    public PermissionsController(
        IGetPermissionCatalogUseCase getPermissionCatalogUseCase,
        IGetModPermissionsUseCase getModPermissionsUseCase,
        IReplaceModPermissionsUseCase replaceModPermissionsUseCase,
        IReplaceModRolePermissionsUseCase replaceModRolePermissionsUseCase)
    {
        _getPermissionCatalogUseCase = getPermissionCatalogUseCase;
        _getModPermissionsUseCase = getModPermissionsUseCase;
        _replaceModPermissionsUseCase = replaceModPermissionsUseCase;
        _replaceModRolePermissionsUseCase = replaceModRolePermissionsUseCase;
    }

    // ADM-P1
    /// <summary>Returns the permission catalog and current MOD and ADMIN defaults.</summary>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The catalog and role defaults were returned.</response>
    /// <response code="401">The access token is missing or invalid (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an administrator (FORBIDDEN).</response>
    [HttpGet("permissions")]
    [ProducesResponseType(typeof(ApiResponse<PermissionCatalogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPermissionCatalog(CancellationToken ct)
    {
        var result = await _getPermissionCatalogUseCase.ExecuteAsync(ct);
        return Ok(new ApiResponse<PermissionCatalogDto> { Success = true, Data = result });
    }

    // ADM-P2
    /// <summary>Returns defaults, overrides, and effective permissions for one MOD account.</summary>
    /// <param name="userId">Identifies the MOD account.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The MOD permission state was returned.</response>
    /// <response code="401">The access token is missing or invalid (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an administrator (FORBIDDEN).</response>
    /// <response code="404">The MOD account does not exist (MOD_NOT_FOUND).</response>
    [HttpGet("mods/{userId:int}/permissions")]
    [ProducesResponseType(typeof(ApiResponse<ModPermissionsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModPermissions(int userId, CancellationToken ct)
    {
        var result = await _getModPermissionsUseCase.ExecuteAsync(userId, ct);
        return Ok(new ApiResponse<ModPermissionsDto> { Success = true, Data = result });
    }

    // ADM-P3
    /// <summary>Replaces every permission override of one MOD account.</summary>
    /// <param name="userId">Identifies the MOD account.</param>
    /// <param name="request">Contains the complete replacement override list.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The overrides were replaced and the resulting permission state was returned.</response>
    /// <response code="400">Validation failed, a code is unknown, or a permission is not grantable.</response>
    /// <response code="401">The access token is missing or invalid (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an administrator (FORBIDDEN).</response>
    /// <response code="404">The MOD account does not exist (MOD_NOT_FOUND).</response>
    [HttpPut("mods/{userId:int}/permissions")]
    [ProducesResponseType(typeof(ApiResponse<ModPermissionsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReplaceModPermissions(
        int userId,
        [FromBody] ReplaceModPermissionsRequestDto request,
        CancellationToken ct)
    {
        var result = await _replaceModPermissionsUseCase.ExecuteAsync(userId, request, ct);
        return Ok(new ApiResponse<ModPermissionsDto> { Success = true, Data = result });
    }

    // ADM-P4
    /// <summary>Replaces every default permission of the MOD role.</summary>
    /// <param name="request">Contains the complete replacement permission-code list.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The MOD role defaults were replaced and returned.</response>
    /// <response code="400">Validation failed, a code is unknown, or a permission is not grantable.</response>
    /// <response code="401">The access token is missing or invalid (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an administrator (FORBIDDEN).</response>
    [HttpPut("roles/MOD/permissions")]
    [ProducesResponseType(typeof(ApiResponse<ReplaceModRolePermissionsResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ReplaceModRolePermissions(
        [FromBody] ReplaceModRolePermissionsRequestDto request,
        CancellationToken ct)
    {
        var result = await _replaceModRolePermissionsUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<ReplaceModRolePermissionsResponseDto> { Success = true, Data = result });
    }
}
