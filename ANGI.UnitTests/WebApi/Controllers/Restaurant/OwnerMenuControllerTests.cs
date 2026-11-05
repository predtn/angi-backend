using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Restaurant;

/// <summary>Verifies OwnerMenuController returns the documented status for each menu workflow action.</summary>
public sealed class OwnerMenuControllerTests
{
    // TEST-01: Return the newly cloned OWN-11 draft in an HTTP 201 ApiResponse.
    /// <summary>Verifies create-submission delegation and response wrapping.</summary>
    [Fact]
    public async Task CreateSubmission_WhenCreated_ShouldReturnCreatedApiResponse()
    {
        // STEP-01: Create expected as MenuSubmissionWithItemsDto.
        var expected = new MenuSubmissionWithItemsDto { Id = 14, Status = "draft" };
        // STEP-02: Mock ICreateMenuSubmissionUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<ICreateMenuSubmissionUseCase>();
        useCase.Setup(item => item.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Build controller under test through CreateController.
        var controller = CreateController(createSubmission: useCase.Object);

        // STEP-04: Run controller.CreateSubmission and keep action.
        var action = await controller.CreateSubmission(CancellationToken.None);

        // STEP-05: Assert action is ObjectResult, result.StatusCode is StatusCodes.Status201Created, result.Value is ApiResponse<MenuSubmissionWithItemsDto> and 1 more.
        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        result.Value.Should().BeOfType<ApiResponse<MenuSubmissionWithItemsDto>>()
            .Which.Data.Should().BeSameAs(expected);
    }

    // TEST-02: Return OWN-16 with HTTP 200 and a null data field.
    /// <summary>Verifies delete-item delegation and the documented empty success payload.</summary>
    [Fact]
    public async Task DeleteItem_WhenDeleted_ShouldReturnOkWithNullData()
    {
        // STEP-01: Mock IDeleteMenuSubmissionItemUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IDeleteMenuSubmissionItemUseCase>();
        useCase.Setup(item => item.ExecuteAsync(14, 340, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // STEP-02: Build controller under test through CreateController.
        var controller = CreateController(deleteItem: useCase.Object);

        // STEP-03: Run controller.DeleteItem and keep action.
        var action = await controller.DeleteItem(14, 340, CancellationToken.None);

        // STEP-04: Assert action is OkObjectResult, result.Value is ApiResponse<object>, response.Success is true and response.Data is null.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeNull();
        // STEP-05: Verify ExecuteAsync is called once.
        useCase.Verify(item => item.ExecuteAsync(14, 340, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Forward the HTTP request cancellation token to the use case.
    /// <summary>Verifies cancellation propagation from the controller to Application.</summary>
    [Fact]
    public async Task GetMenu_ShouldForwardRequestCancellationToken()
    {
        // STEP-01: Create cancellation as CancellationTokenSource.
        using var cancellation = new CancellationTokenSource();
        // STEP-02: Mock IGetOwnerMenuUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IGetOwnerMenuUseCase>();
        useCase.Setup(item => item.ExecuteAsync(cancellation.Token))
            .ReturnsAsync(new OwnerMenuDto());
        // STEP-03: Build controller under test through CreateController.
        var controller = CreateController(getMenu: useCase.Object);

        // STEP-04: Run controller.GetMenu.
        await controller.GetMenu(cancellation.Token);

        // STEP-05: Verify ExecuteAsync is called once.
        useCase.Verify(item => item.ExecuteAsync(cancellation.Token), Times.Once);
    }

    // TEST-04: Return the submitted OWN-17 menu in an HTTP 200 ApiResponse.
    /// <summary>Verifies submit-menu delegation, request forwarding, and response wrapping.</summary>
    [Fact]
    public async Task SubmitSubmission_WhenSubmitted_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create request as SubmitMenuSubmissionRequestDto and create expected as MenuSubmissionDto.
        var request = new SubmitMenuSubmissionRequestDto { OwnerNote = "Gửi duyệt" };
        var expected = new MenuSubmissionDto { Id = 14, Status = "pending" };
        // STEP-02: Mock ISubmitMenuSubmissionUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<ISubmitMenuSubmissionUseCase>();
        useCase.Setup(value => value.ExecuteAsync(14, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        // STEP-03: Build controller under test through CreateController.
        var controller = CreateController(submitSubmission: useCase.Object);

        // STEP-04: Run controller.SubmitSubmission with request and keep action.
        var action = await controller.SubmitSubmission(14, request, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<MenuSubmissionDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<MenuSubmissionDto>>()
            .Which.Data.Should().BeSameAs(expected);
        // STEP-06: Verify ExecuteAsync is called once.
        useCase.Verify(
            value => value.ExecuteAsync(14, request, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Creates the controller while allowing the test to replace selected dependencies.</summary>
    private static OwnerMenuController CreateController(
        IGetOwnerMenuUseCase? getMenu = null,
        ICreateMenuSubmissionUseCase? createSubmission = null,
        IDeleteMenuSubmissionItemUseCase? deleteItem = null,
        ISubmitMenuSubmissionUseCase? submitSubmission = null)
    {
        return new OwnerMenuController(
            getMenu ?? Mock.Of<IGetOwnerMenuUseCase>(),
            Mock.Of<IUpdateDishAvailabilityUseCase>(),
            createSubmission ?? Mock.Of<ICreateMenuSubmissionUseCase>(),
            Mock.Of<IListMenuSubmissionsUseCase>(),
            Mock.Of<IGetMenuSubmissionUseCase>(),
            Mock.Of<IAddMenuSubmissionItemUseCase>(),
            Mock.Of<IUpdateMenuSubmissionItemUseCase>(),
            deleteItem ?? Mock.Of<IDeleteMenuSubmissionItemUseCase>(),
            submitSubmission ?? Mock.Of<ISubmitMenuSubmissionUseCase>(),
            Mock.Of<ICancelMenuSubmissionUseCase>());
    }
}
