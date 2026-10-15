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

        var action = await new DishesController(Mock.Of<IBrowseDishesUseCase>(), useCase.Object, Mock.Of<IRollDishUseCase>()).GetDetail(105, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = result.Value.Should().BeOfType<ApiResponse<DishDetailDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
    }

    // TEST-03: DISH-01 is public too and wraps its page in a 200 ApiResponse.
    [Fact]
    public async Task Browse_ShouldAllowAnonymousAndReturnOk()
    {
        typeof(DishesController).GetMethod(nameof(DishesController.Browse))!
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().ContainSingle();
        var request = new BrowseDishesRequestDto { Q = "bún" };
        var expected = new BrowseDishesResponseDto { RequestId = Guid.NewGuid(), NextCursor = "next" };
        var useCase = new Mock<IBrowseDishesUseCase>();
        useCase.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var action = await new DishesController(useCase.Object, Mock.Of<IGetDishDetailUseCase>(), Mock.Of<IRollDishUseCase>()).Browse(request, CancellationToken.None);

        var response = action.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ApiResponse<BrowseDishesResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
    }

    // TEST-04: DISH-03 is for Travelers only and wraps the roll in a 200 ApiResponse.
    [Fact]
    public async Task Roll_ShouldRequireTravelerAndReturnOk()
    {
        var action = typeof(DishesController).GetMethod(nameof(DishesController.Roll))!;
        action.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>()
            .Should().ContainSingle().Which.Roles.Should().Be("TRAVELER");
        var request = new RollDishRequestDto { MealTime = "lunch" };
        var expected = new RollDishResponseDto { RollId = 4410, Dish = new DishDetailDto { Id = 105 } };
        var useCase = new Mock<IRollDishUseCase>();
        useCase.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await new DishesController(Mock.Of<IBrowseDishesUseCase>(), Mock.Of<IGetDishDetailUseCase>(), useCase.Object)
            .Roll(request, CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ApiResponse<RollDishResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
    }
}
