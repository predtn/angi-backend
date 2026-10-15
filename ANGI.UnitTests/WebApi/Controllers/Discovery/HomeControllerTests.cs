using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Discovery;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Discovery;

public sealed class HomeControllerTests
{
    // TEST-01: HOME-01 is public (token optional) and wraps the blocks in a 200 ApiResponse.
    [Fact]
    public async Task Get_ShouldAllowAnonymousAndReturnOk()
    {
        typeof(HomeController).GetMethod(nameof(HomeController.Get))!
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().ContainSingle();
        var expected = new GetHomeResponseDto();
        var useCase = new Mock<IGetHomeUseCase>();
        useCase.Setup(x => x.ExecuteAsync(It.IsAny<GetHomeRequestDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var action = await new HomeController(useCase.Object).Get(new GetHomeRequestDto(), CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ApiResponse<GetHomeResponseDto>>().Which.Data.Should().BeSameAs(expected);
    }
}
