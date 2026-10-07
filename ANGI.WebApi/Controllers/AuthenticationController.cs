using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers
{
    [ApiController]
    [Route("api/v1/auth")]
    public sealed class AuthenticationController : ControllerBase
    {
        private readonly ILoginUseCase _loginUseCase;
        private readonly IRefreshTokenUseCase _refreshTokenUseCase;
        private readonly ILogoutUseCase _logoutUseCase;

        /// <summary>Initializes the HTTP boundary with the three Auth use-case abstractions.</summary>
        public AuthenticationController(
            ILoginUseCase loginUseCase,
            IRefreshTokenUseCase refreshTokenUseCase,
            ILogoutUseCase logoutUseCase)
        {
            _loginUseCase = loginUseCase;
            _refreshTokenUseCase = refreshTokenUseCase;
            _logoutUseCase = logoutUseCase;
        }

        /// <summary>Receives an AUTH-04 request, invokes LoginUseCase, and wraps the result in ApiResponse.</summary>
        [AllowAnonymous]
        [HttpPost("login")]
        [ProducesResponseType(typeof(ApiResponse<AuthResultDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Login(LoginRequestDto request, CancellationToken ct)
        {
            var result = await _loginUseCase.ExecuteAsync(request, ct);
            return Ok(new ApiResponse<AuthResultDto>
            {
                Success = true,
                Data = result
            });
        }

        /// <summary>Receives an AUTH-06 request, invokes RefreshTokenUseCase, and returns the rotated token pair.</summary>
        [AllowAnonymous]
        [HttpPost("refresh")]
        [ProducesResponseType(typeof(ApiResponse<AuthResultDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Refresh(RefreshTokenRequestDto request, CancellationToken ct)
        {
            var result = await _refreshTokenUseCase.ExecuteAsync(request, ct);
            return Ok(new ApiResponse<AuthResultDto>
            {
                Success = true,
                Data = result
            });
        }

        /// <summary>Receives an AUTH-07 request, revokes the current session, and returns null data.</summary>
        [Authorize]
        [HttpPost("logout")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Logout(LogoutRequestDto request, CancellationToken ct)
        {
            await _logoutUseCase.ExecuteAsync(request, ct);
            return Ok(new ApiResponse<object>
            {
                Success = true,
                Data = null
            });
        }
    }
}
