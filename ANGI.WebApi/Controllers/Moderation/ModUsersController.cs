using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes user moderation endpoints to Mod and Admin accounts.</summary>
[ApiController]
[Route("api/v1/mod/users")]
[Authorize(Roles = "MOD,ADMIN,MODERATOR")]
public sealed class ModUsersController(
    IGetUsersUseCase getUsers,
    IGetUserDetailUseCase getUserDetail) : ControllerBase
{
    /// <summary>MOD-U1: lists user accounts using moderator filters and pagination.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<AdminUserCardDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] ListUsersRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<Paged<AdminUserCardDto>> { Success = true, Data = await getUsers.ExecuteAsync(request, ct) });

    /// <summary>MOD-U2: returns user profile details, sanction history, content stats and restaurant if owner.</summary>
    [HttpGet("{userId:int}")]
    [ProducesResponseType(typeof(ApiResponse<ModUserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int userId, CancellationToken ct) =>
        Ok(new ApiResponse<ModUserDetailDto> { Success = true, Data = await getUserDetail.ExecuteAsync(userId, ct) });
}
