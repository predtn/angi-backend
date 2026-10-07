using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.Test.WebApi.Controllers
{
    public sealed class AuthenticationControllerTests
    {
        // TEST-01: Wrap a successful login result in the standard API response.
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

        // TEST-02: Return a successful API response with null data after logout.
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

        // TEST-03: Wrap a successful token refresh in the documented authentication API response.
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

        // TEST-04: Expose login and refresh anonymously while requiring authentication for logout.
        [Fact]
        public void AuthenticationEndpoints_ShouldDeclareDocumentedAuthorizationAttributes()
        {
            var controllerType = typeof(AuthenticationController);

            controllerType.GetMethod(nameof(AuthenticationController.Login))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.Refresh))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.Logout))!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Should().ContainSingle();
        }

        private static AuthenticationController CreateController(
            Mock<ILoginUseCase>? loginUseCase = null,
            Mock<IRefreshTokenUseCase>? refreshTokenUseCase = null,
            Mock<ILogoutUseCase>? logoutUseCase = null)
        {
            return new AuthenticationController(
                (loginUseCase ?? new Mock<ILoginUseCase>()).Object,
                (refreshTokenUseCase ?? new Mock<IRefreshTokenUseCase>()).Object,
                (logoutUseCase ?? new Mock<ILogoutUseCase>()).Object);
        }
    }
}
