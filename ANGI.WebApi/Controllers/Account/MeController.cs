using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.DTOs.Account;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Account;

/// <summary>Exposes personal profile and account management endpoints for the authenticated user.</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    private readonly IChangePasswordUseCase _changePasswordUseCase;

    /// <summary>Initializes the MeController with account use-case abstractions.</summary>
    public MeController(IChangePasswordUseCase changePasswordUseCase)
    {
        _changePasswordUseCase = changePasswordUseCase;
    }

    // ME-03
    /// <summary>Receives an ME-03 request and changes the authenticated user's password while revoking other sessions.</summary>
    [HttpPut("password")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(
        [FromBody, BindRequired] ChangePasswordRequestDto request,
        CancellationToken ct)
    {
        await _changePasswordUseCase.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status200OK, new ApiResponse<object>
        {
            Success = true,
            Data = null
        });
    }
}
