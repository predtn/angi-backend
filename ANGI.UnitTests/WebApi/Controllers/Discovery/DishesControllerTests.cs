using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Discovery;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Discovery;

public sealed class DishesControllerTests
{
    // TEST-01: DISH-02 is public ("Public (token tùy chọn)"): anonymous access and no role restriction.
    [Fact]
    public void GetDetail_ShouldAllowAnonymousCallers()
    {
        var action = typeof(DishesController).GetMethod(nameof(DishesController.GetDetail))!;

        action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().ContainSingle();
        typeof(DishesController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Should().BeEmpty();
    }

    // TEST-02: Wrap the DISH-02 result in a 200 ApiResponse.
    [Fact]
    public async Task GetDetail_ShouldReturnOkWithUseCaseResult()
    {
        var expected = new DishDetailDto { Id = 105, Name = "Bún bò Huế", Status = "active" };
        var useCase = new Mock<IGetDishDetailUseCase>();
        useCase.Setup(x => x.ExecuteAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var action = await new DishesController(useCase.Object).GetDetail(105, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = result.Value.Should().BeOfType<ApiResponse<DishDetailDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
    }
}
