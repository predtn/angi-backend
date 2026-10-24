using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Administration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Administration;

public sealed class ModsControllerTests
{
    // TEST-01: Return the ADM-M1 page in an HTTP 200 API envelope.
    /// <summary>Verifies list delegation and response typing.</summary>
    [Fact]
    public async Task List_ShouldReturnOkApiResponse()
    {
        var request = new ListModsRequestDto();
        var expected = new Paged<AdminUserCardDto>();
        var useCase = new Mock<IListModsUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = Create(list: useCase.Object);

        var action = await controller.List(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<Paged<AdminUserCardDto>>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-02: Return the ADM-M2 card in an HTTP 201 API envelope.
    /// <summary>Verifies creation uses the exact documented status rather than HTTP 200.</summary>
    [Fact]
    public async Task Create_ShouldReturnCreatedApiResponse()
    {
        var request = new CreateModRequestDto();
        var expected = new AdminUserCardDto();
        var useCase = new Mock<ICreateModUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = Create(create: useCase.Object);

        var action = await controller.Create(request, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<AdminUserCardDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-03: Pass the ADM-M3 route ID and return its HTTP 200 API envelope.
    /// <summary>Verifies detail route binding and response typing.</summary>
    [Fact]
    public async Task Get_ShouldDelegateUserIdAndReturnOkApiResponse()
    {
        var expected = new ModDetailDto();
        var useCase = new Mock<IGetModDetailUseCase>();
        useCase.Setup(item => item.ExecuteAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = Create(get: useCase.Object);

        var action = await controller.Get(42, CancellationToken.None);

        useCase.VerifyAll();
        action.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ApiResponse<ModDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-04: Pass the ADM-M4 route and body and return its HTTP 200 API envelope.
    /// <summary>Verifies update delegation and response typing.</summary>
    [Fact]
    public async Task Update_ShouldDelegateRequestAndReturnOkApiResponse()
    {
        var request = new UpdateModRequestDto { Status = "deactivated" };
        var expected = new AdminUserCardDto();
        var useCase = new Mock<IUpdateModUseCase>();
        useCase.Setup(item => item.ExecuteAsync(42, request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = Create(update: useCase.Object);

        var action = await controller.Update(42, request, CancellationToken.None);

        useCase.VerifyAll();
        action.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ApiResponse<AdminUserCardDto>>().Which.Data.Should().BeSameAs(expected);
    }

    /// <summary>Creates the controller with optional strict dependencies for one action at a time.</summary>
    private static ModsController Create(
        IListModsUseCase? list = null,
        ICreateModUseCase? create = null,
        IGetModDetailUseCase? get = null,
        IUpdateModUseCase? update = null) => new(
            list ?? Mock.Of<IListModsUseCase>(),
            create ?? Mock.Of<ICreateModUseCase>(),
            get ?? Mock.Of<IGetModDetailUseCase>(),
            update ?? Mock.Of<IUpdateModUseCase>());
}
