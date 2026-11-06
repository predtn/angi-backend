using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Administration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Administration;

/// <summary>Verifies ModsController passes route and body data to the MOD use cases and returns the documented status codes.</summary>
public sealed class ModsControllerTests
{
    // TEST-01: Return the ADM-M1 page in an HTTP 200 API envelope.
    /// <summary>Verifies list delegation and response typing.</summary>
    [Fact]
    public async Task List_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create request as ListModsRequestDto and create expected as Paged<AdminUserCardDto>.
        var request = new ListModsRequestDto();
        var expected = new Paged<AdminUserCardDto>();
        // STEP-02: Mock IListModsUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IListModsUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Build controller under test through Create.
        var controller = Create(list: useCase.Object);

        // STEP-04: Run controller.List with request and keep action.
        var action = await controller.List(request, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<Paged<AdminUserCardDto>> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<Paged<AdminUserCardDto>>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-02: Return the ADM-M2 card in an HTTP 201 API envelope.
    /// <summary>Verifies creation uses the exact documented status rather than HTTP 200.</summary>
    [Fact]
    public async Task Create_ShouldReturnCreatedApiResponse()
    {
        // STEP-01: Create request as CreateModRequestDto and create expected as AdminUserCardDto.
        var request = new CreateModRequestDto();
        var expected = new AdminUserCardDto();
        // STEP-02: Mock ICreateModUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<ICreateModUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Build controller under test through Create.
        var controller = Create(create: useCase.Object);

        // STEP-04: Run controller.Create with request and keep action.
        var action = await controller.Create(request, CancellationToken.None);

        // STEP-05: Assert action is ObjectResult, result.StatusCode is 201, result.Value is ApiResponse<AdminUserCardDto> and 1 more.
        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<AdminUserCardDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-03: Pass the ADM-M3 route ID and return its HTTP 200 API envelope.
    /// <summary>Verifies detail route binding and response typing.</summary>
    [Fact]
    public async Task Get_ShouldDelegateUserIdAndReturnOkApiResponse()
    {
        // STEP-01: Create expected as ModDetailDto.
        var expected = new ModDetailDto();
        // STEP-02: Mock IGetModDetailUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IGetModDetailUseCase>();
        useCase.Setup(item => item.ExecuteAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Build controller under test through Create.
        var controller = Create(get: useCase.Object);

        // STEP-04: Run controller.Get and keep action.
        var action = await controller.Get(42, CancellationToken.None);

        // STEP-05: Verify every setup on useCase ran.
        useCase.VerifyAll();
        // STEP-06: Assert action is OkObjectResult, action.Value is ApiResponse<ModDetailDto> and action.Value.Data is the same instance as expected.
        action.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ApiResponse<ModDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-04: Pass the ADM-M4 route and body and return its HTTP 200 API envelope.
    /// <summary>Verifies update delegation and response typing.</summary>
    [Fact]
    public async Task Update_ShouldDelegateRequestAndReturnOkApiResponse()
    {
        // STEP-01: Create request as UpdateModRequestDto and create expected as AdminUserCardDto.
        var request = new UpdateModRequestDto { Status = "deactivated" };
        var expected = new AdminUserCardDto();
        // STEP-02: Mock IUpdateModUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IUpdateModUseCase>();
        useCase.Setup(item => item.ExecuteAsync(42, request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Build controller under test through Create.
        var controller = Create(update: useCase.Object);

        // STEP-04: Run controller.Update with request and keep action.
        var action = await controller.Update(42, request, CancellationToken.None);

        // STEP-05: Verify every setup on useCase ran.
        useCase.VerifyAll();
        // STEP-06: Assert action is OkObjectResult, action.Value is ApiResponse<AdminUserCardDto> and action.Value.Data is the same instance as expected.
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
