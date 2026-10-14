using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Auth
{
    [ApiController]
    [Route("api/v1/auth")]
    public sealed class AuthenticationController : ControllerBase
    {
        private readonly IRegisterAccountUseCase _registerAccountUseCase;
        private readonly ILoginUseCase _loginUseCase;
        private readonly IGoogleLoginUseCase _googleLoginUseCase;
        private readonly IRefreshTokenUseCase _refreshTokenUseCase;
        private readonly ILogoutUseCase _logoutUseCase;
        private readonly IVerifyEmailUseCase _verifyEmailUseCase;
        private readonly IResendVerificationEmailUseCase _resendVerificationEmailUseCase;
        private readonly IForgotPasswordUseCase _forgotPasswordUseCase;

        /// <summary>Initializes the HTTP boundary with the Auth use-case abstractions.</summary>
        public AuthenticationController(
            IRegisterAccountUseCase registerAccountUseCase,
            ILoginUseCase loginUseCase,
            IGoogleLoginUseCase googleLoginUseCase,
            IRefreshTokenUseCase refreshTokenUseCase,
            ILogoutUseCase logoutUseCase,
            IVerifyEmailUseCase verifyEmailUseCase,
            IResendVerificationEmailUseCase resendVerificationEmailUseCase,
            IForgotPasswordUseCase forgotPasswordUseCase)
        {
            _registerAccountUseCase = registerAccountUseCase;
            _loginUseCase = loginUseCase;
            _googleLoginUseCase = googleLoginUseCase;
            _refreshTokenUseCase = refreshTokenUseCase;
            _logoutUseCase = logoutUseCase;
            _verifyEmailUseCase = verifyEmailUseCase;
            _resendVerificationEmailUseCase = resendVerificationEmailUseCase;
            _forgotPasswordUseCase = forgotPasswordUseCase;
        }

        /// <summary>Receives an AUTH-02 request, consumes its token, and returns the verified account state.</summary>
        [AllowAnonymous]
        [HttpPost("verify-email")]
        [ProducesResponseType(typeof(ApiResponse<VerifyEmailResponseDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> VerifyEmail(
            [FromBody, BindRequired] VerifyEmailRequestDto request,
            CancellationToken ct)
        {
            var result = await _verifyEmailUseCase.ExecuteAsync(request, ct);
            return Ok(new ApiResponse<VerifyEmailResponseDto>
            {
                Success = true,
                Data = result
            });
        }

        /// <summary>Receives an AUTH-03 request, conditionally sends a fresh link, and always returns null data.</summary>
        [AllowAnonymous]
        [HttpPost("verify-email/resend")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ResendVerificationEmail(
            [FromBody, BindRequired] ResendVerificationEmailRequestDto request,
            CancellationToken ct)
        {
            await _resendVerificationEmailUseCase.ExecuteAsync(request, ct);
            return Ok(new ApiResponse<object>
            {
                Success = true,
                Data = null
            });
        }

        /// <summary>Receives an AUTH-08 request, conditionally sends a reset link, and always returns null data.</summary>
        [AllowAnonymous]
        [HttpPost("forgot-password")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ForgotPassword(
            [FromBody, BindRequired] ForgotPasswordRequestDto request,
            CancellationToken ct)
        {
            await _forgotPasswordUseCase.ExecuteAsync(request, ct);
            return Ok(new ApiResponse<object>
            {
                Success = true,
                Data = null
            });
        }

        /// <summary>Receives an AUTH-01 request, creates a pending account, and returns the documented 201 response.</summary>
        [AllowAnonymous]
        [HttpPost("register")]
        [ProducesResponseType(typeof(ApiResponse<RegisterAccountResponseDto>), StatusCodes.Status201Created)]
        public async Task<IActionResult> Register(RegisterAccountRequestDto request, CancellationToken ct)
        {
            var result = await _registerAccountUseCase.ExecuteAsync(request, ct);
            return StatusCode(StatusCodes.Status201Created, new ApiResponse<RegisterAccountResponseDto>
            {
                Success = true,
                Data = result
            });
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

        /// <summary>Receives an AUTH-05 request and returns the linked or newly created Google account session.</summary>
        [AllowAnonymous]
        [HttpPost("google")]
        [ProducesResponseType(typeof(ApiResponse<GoogleLoginResponseDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GoogleLogin(GoogleLoginRequestDto request, CancellationToken ct)
        {
            var result = await _googleLoginUseCase.ExecuteAsync(request, ct);
            return Ok(new ApiResponse<GoogleLoginResponseDto>
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
