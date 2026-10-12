using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.Test.WebApi.Controllers.Restaurant;

public sealed class OwnerMenuControllerTests
{
    // TEST-01: Return the newly cloned OWN-11 draft in an HTTP 201 ApiResponse.
    /// <summary>Verifies create-submission delegation and response wrapping.</summary>
    [Fact]
    public async Task CreateSubmission_WhenCreated_ShouldReturnCreatedApiResponse()
    {
        var expected = new MenuSubmissionWithItemsDto { Id = 14, Status = "draft" };
        var useCase = new Mock<ICreateMenuSubmissionUseCase>();
        useCase.Setup(item => item.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = CreateController(createSubmission: useCase.Object);

        var action = await controller.CreateSubmission(CancellationToken.None);

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
        var useCase = new Mock<IDeleteMenuSubmissionItemUseCase>();
        useCase.Setup(item => item.ExecuteAsync(14, 340, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var controller = CreateController(deleteItem: useCase.Object);

        var action = await controller.DeleteItem(14, 340, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeNull();
        useCase.Verify(item => item.ExecuteAsync(14, 340, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Forward the HTTP request cancellation token to the use case.
    [Fact]
    public async Task GetMenu_ShouldForwardRequestCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var useCase = new Mock<IGetOwnerMenuUseCase>();
        useCase.Setup(item => item.ExecuteAsync(cancellation.Token))
            .ReturnsAsync(new OwnerMenuDto());
        var controller = CreateController(getMenu: useCase.Object);

        await controller.GetMenu(cancellation.Token);

        useCase.Verify(item => item.ExecuteAsync(cancellation.Token), Times.Once);
    }

    /// <summary>Creates the controller while allowing the test to replace selected dependencies.</summary>
    private static OwnerMenuController CreateController(
        IGetOwnerMenuUseCase? getMenu = null,
        ICreateMenuSubmissionUseCase? createSubmission = null,
        IDeleteMenuSubmissionItemUseCase? deleteItem = null)
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
            Mock.Of<ICancelMenuSubmissionUseCase>());
    }
}
