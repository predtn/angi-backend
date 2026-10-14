using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Auth
{
    public sealed class AuthenticationControllerTests
    {
        // TEST-01: Return the documented 201 response after registering an account.
        /// <summary>Verifies AUTH-01 is wrapped in ApiResponse with HTTP 201.</summary>
        [Fact]
        public async Task Register_ShouldReturnCreatedApiResponse()
        {
            var expected = new RegisterAccountResponseDto { UserId = 12, Status = "pending_verification" };
            var registerUseCase = new Mock<IRegisterAccountUseCase>();
            registerUseCase.Setup(x => x.ExecuteAsync(
                    It.IsAny<RegisterAccountRequestDto>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(registerAccountUseCase: registerUseCase);

            var action = await controller.Register(new RegisterAccountRequestDto(), CancellationToken.None);

            var result = action.Should().BeOfType<ObjectResult>().Subject;
            result.StatusCode.Should().Be(StatusCodes.Status201Created);
            var response = result.Value.Should().BeOfType<ApiResponse<RegisterAccountResponseDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-02: Wrap a successful login result in the standard API response.
        /// <summary>Verifies AUTH-04 returns the login result in the standard success envelope.</summary>
        [Fact]
        public async Task Login_ShouldReturnOkApiResponse()
        {
            var expected = new AuthResultDto { AccessToken = "access-token" };
            var loginUseCase = new Mock<ILoginUseCase>();
            loginUseCase.Setup(x => x.ExecuteAsync(It.IsAny<LoginRequestDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(loginUseCase: loginUseCase);

            var action = await controller.Login(new LoginRequestDto(), CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<AuthResultDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-03: Return a successful API response with null data after logout.
        /// <summary>Verifies AUTH-07 returns null data after delegating logout.</summary>
        [Fact]
        public async Task Logout_ShouldReturnOkApiResponseWithNullData()
        {
            var logoutUseCase = new Mock<ILogoutUseCase>();
            var controller = CreateController(logoutUseCase: logoutUseCase);

            var action = await controller.Logout(
                new LogoutRequestDto { RefreshToken = "refresh-token" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            result.StatusCode.Should().Be(StatusCodes.Status200OK);
            var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeNull();
            logoutUseCase.Verify(
                x => x.ExecuteAsync(It.IsAny<LogoutRequestDto>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // TEST-04: Wrap a successful token refresh in the documented authentication API response.
        /// <summary>Verifies AUTH-06 returns the rotated token result in the standard success envelope.</summary>
        [Fact]
        public async Task Refresh_ShouldReturnOkApiResponse()
        {
            var expected = new AuthResultDto { RefreshToken = "new-refresh-token" };
            var refreshUseCase = new Mock<IRefreshTokenUseCase>();
            refreshUseCase.Setup(x => x.ExecuteAsync(It.IsAny<RefreshTokenRequestDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(refreshTokenUseCase: refreshUseCase);

            var action = await controller.Refresh(
                new RefreshTokenRequestDto { RefreshToken = "old-refresh-token" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<AuthResultDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-05: Wrap a successful email verification in the documented API response.
        /// <summary>Verifies AUTH-02 returns the verified account state from its use case.</summary>
        [Fact]
        public async Task VerifyEmail_ShouldReturnOkApiResponse()
        {
            var expected = new VerifyEmailResponseDto { UserId = 12, Status = "active" };
            var verifyEmailUseCase = new Mock<IVerifyEmailUseCase>();
            verifyEmailUseCase.Setup(x => x.ExecuteAsync(
                    It.IsAny<VerifyEmailRequestDto>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(verifyEmailUseCase: verifyEmailUseCase);

            var action = await controller.VerifyEmail(
                new VerifyEmailRequestDto { Token = "raw-token" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<VerifyEmailResponseDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-06: Return a successful null-data response after requesting a verification resend.
        /// <summary>Verifies AUTH-03 keeps the response identical regardless of account existence.</summary>
        [Fact]
        public async Task ResendVerificationEmail_ShouldReturnOkApiResponseWithNullData()
        {
            var resendUseCase = new Mock<IResendVerificationEmailUseCase>();
            var controller = CreateController(resendVerificationEmailUseCase: resendUseCase);

            var action = await controller.ResendVerificationEmail(
                new ResendVerificationEmailRequestDto { Email = "user@angi.test" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeNull();
            resendUseCase.Verify(x => x.ExecuteAsync(
                It.IsAny<ResendVerificationEmailRequestDto>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-07: Return a successful null-data response after requesting a password reset.
        /// <summary>Verifies AUTH-08 delegates to its use case and preserves the non-enumerating response shape.</summary>
        [Fact]
        public async Task ForgotPassword_ShouldReturnOkApiResponseWithNullData()
        {
            var forgotPasswordUseCase = new Mock<IForgotPasswordUseCase>();
            var controller = CreateController(forgotPasswordUseCase: forgotPasswordUseCase);

            var action = await controller.ForgotPassword(
                new ForgotPasswordRequestDto { Email = "user@angi.test" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeNull();
            forgotPasswordUseCase.Verify(x => x.ExecuteAsync(
                It.IsAny<ForgotPasswordRequestDto>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-08: Return a successful null-data response after resetting a password.
        /// <summary>Verifies AUTH-09 delegates to its use case and returns the documented success envelope.</summary>
        [Fact]
        public async Task ResetPassword_ShouldReturnOkApiResponseWithNullData()
        {
            var resetPasswordUseCase = new Mock<IResetPasswordUseCase>();
            var controller = CreateController(resetPasswordUseCase: resetPasswordUseCase);

            var action = await controller.ResetPassword(
                new ResetPasswordRequestDto { Token = "raw-token", NewPassword = "Password2" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeNull();
            resetPasswordUseCase.Verify(x => x.ExecuteAsync(
                It.IsAny<ResetPasswordRequestDto>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-09: Wrap a successful Google login result in the documented AUTH-05 response.
        /// <summary>Verifies AUTH-05 exposes AuthResult fields and isNewUser through ApiResponse.</summary>
        [Fact]
        public async Task GoogleLogin_ShouldReturnOkApiResponse()
        {
            var expected = new GoogleLoginResponseDto { AccessToken = "access-token", IsNewUser = true };
            var googleLoginUseCase = new Mock<IGoogleLoginUseCase>();
            googleLoginUseCase.Setup(x => x.ExecuteAsync(
                    It.IsAny<GoogleLoginRequestDto>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(googleLoginUseCase: googleLoginUseCase);

            var action = await controller.GoogleLogin(
                new GoogleLoginRequestDto { IdToken = "google-token", Role = "TRAVELER" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<GoogleLoginResponseDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-10: Expose public authentication endpoints anonymously while requiring authentication for logout.
        /// <summary>Verifies the controller authorization attributes match the Auth API contract.</summary>
        [Fact]
        public void AuthenticationEndpoints_ShouldDeclareDocumentedAuthorizationAttributes()
        {
            var controllerType = typeof(AuthenticationController);

            controllerType.GetMethod(nameof(AuthenticationController.Register))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.Login))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.GoogleLogin))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.Refresh))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.VerifyEmail))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.ResendVerificationEmail))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.ForgotPassword))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.ResetPassword))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.Logout))!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Should().ContainSingle();
        }

        /// <summary>Creates the controller with supplied mocks and harmless defaults for remaining dependencies.</summary>
        private static AuthenticationController CreateController(
            Mock<IRegisterAccountUseCase>? registerAccountUseCase = null,
            Mock<ILoginUseCase>? loginUseCase = null,
            Mock<IGoogleLoginUseCase>? googleLoginUseCase = null,
            Mock<IRefreshTokenUseCase>? refreshTokenUseCase = null,
            Mock<ILogoutUseCase>? logoutUseCase = null,
            Mock<IVerifyEmailUseCase>? verifyEmailUseCase = null,
            Mock<IResendVerificationEmailUseCase>? resendVerificationEmailUseCase = null,
            Mock<IForgotPasswordUseCase>? forgotPasswordUseCase = null,
            Mock<IResetPasswordUseCase>? resetPasswordUseCase = null)
        {
            return new AuthenticationController(
                (registerAccountUseCase ?? new Mock<IRegisterAccountUseCase>()).Object,
                (loginUseCase ?? new Mock<ILoginUseCase>()).Object,
                (googleLoginUseCase ?? new Mock<IGoogleLoginUseCase>()).Object,
                (refreshTokenUseCase ?? new Mock<IRefreshTokenUseCase>()).Object,
                (logoutUseCase ?? new Mock<ILogoutUseCase>()).Object,
                (verifyEmailUseCase ?? new Mock<IVerifyEmailUseCase>()).Object,
                (resendVerificationEmailUseCase ?? new Mock<IResendVerificationEmailUseCase>()).Object,
                (forgotPasswordUseCase ?? new Mock<IForgotPasswordUseCase>()).Object,
                (resetPasswordUseCase ?? new Mock<IResetPasswordUseCase>()).Object);
        }
    }
}
