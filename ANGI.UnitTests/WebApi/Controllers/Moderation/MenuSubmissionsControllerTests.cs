using System.Reflection;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Moderation;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Moderation;

/// <summary>Verifies menu controller routing, role protection, result envelopes and delegation.</summary>
public sealed class MenuSubmissionsControllerTests
{
    // TEST-01: Forward MOD-M1 filters and cancellation and wrap the exact use-case result in a successful 200 response.
    /// <summary>Verifies queue delegation without business logic in the controller.</summary>
    [Fact]
    public async Task List_ShouldDelegate()
    {
        using var cancellation = new CancellationTokenSource();
        var request = new ListModMenuSubmissionsRequestDto();
        var result = new Paged<ModMenuSubmissionCardDto>();
        var list = new Mock<IListModMenuSubmissionsUseCase>();
        list.Setup(x => x.ExecuteAsync(request, cancellation.Token)).ReturnsAsync(result);
        var controller = new MenuSubmissionsController(list.Object, Mock.Of<IGetMenuSubmissionReviewUseCase>(),
            Mock.Of<IApproveMenuSubmissionUseCase>(), Mock.Of<IRejectMenuSubmissionUseCase>());
        var response = (OkObjectResult)await controller.List(request, cancellation.Token);
        response.StatusCode.Should().Be(200);
        ((ApiResponse<Paged<ModMenuSubmissionCardDto>>)response.Value!).Data.Should().BeSameAs(result);
        list.Verify(x => x.ExecuteAsync(request, cancellation.Token), Times.Once);
    }

    // TEST-02: Forward MOD-M2 identifiers and cancellation and return the flattened detail response.
    /// <summary>Verifies detail delegation and successful response wrapping.</summary>
    [Fact]
    public async Task Detail_ShouldDelegate()
    {
        using var cancellation = new CancellationTokenSource();
        var result = new ModMenuSubmissionDetailDto();
        var detail = new Mock<IGetMenuSubmissionReviewUseCase>();
        detail.Setup(x => x.ExecuteAsync(14, cancellation.Token)).ReturnsAsync(result);
        var controller = new MenuSubmissionsController(Mock.Of<IListModMenuSubmissionsUseCase>(), detail.Object,
            Mock.Of<IApproveMenuSubmissionUseCase>(), Mock.Of<IRejectMenuSubmissionUseCase>());
        var response = (OkObjectResult)await controller.Detail(14, cancellation.Token);
        ((ApiResponse<ModMenuSubmissionDetailDto>)response.Value!).Should().BeEquivalentTo(new { Success = true, Data = result });
        detail.Verify(x => x.ExecuteAsync(14, cancellation.Token), Times.Once);
    }

    // TEST-03: Forward MOD-M3 approval notes, identifier and cancellation unchanged.
    /// <summary>Verifies approval delegation and affected-dish response wrapping.</summary>
    [Fact]
    public async Task Approve_ShouldDelegate()
    {
        using var cancellation = new CancellationTokenSource();
        var request = new ApproveMenuSubmissionRequestDto { ReviewNote = "Approved" };
        var result = new ApproveMenuSubmissionResponseDto();
        var approve = new Mock<IApproveMenuSubmissionUseCase>();
        approve.Setup(x => x.ExecuteAsync(14, request, cancellation.Token)).ReturnsAsync(result);
        var controller = new MenuSubmissionsController(Mock.Of<IListModMenuSubmissionsUseCase>(), Mock.Of<IGetMenuSubmissionReviewUseCase>(),
            approve.Object, Mock.Of<IRejectMenuSubmissionUseCase>());
        var response = (OkObjectResult)await controller.Approve(14, request, cancellation.Token);
        ((ApiResponse<ApproveMenuSubmissionResponseDto>)response.Value!).Should().BeEquivalentTo(new { Success = true, Data = result });
        approve.Verify(x => x.ExecuteAsync(14, request, cancellation.Token), Times.Once);
    }

    // TEST-04: Forward MOD-M4 rejection notes, identifier and cancellation unchanged.
    /// <summary>Verifies rejection delegation and successful response wrapping.</summary>
    [Fact]
    public async Task Reject_ShouldDelegate()
    {
        using var cancellation = new CancellationTokenSource();
        var request = new RejectMenuSubmissionRequestDto { ReviewNote = "Reason" };
        var result = new MenuSubmissionDto();
        var reject = new Mock<IRejectMenuSubmissionUseCase>();
        reject.Setup(x => x.ExecuteAsync(14, request, cancellation.Token)).ReturnsAsync(result);
        var controller = new MenuSubmissionsController(Mock.Of<IListModMenuSubmissionsUseCase>(), Mock.Of<IGetMenuSubmissionReviewUseCase>(),
            Mock.Of<IApproveMenuSubmissionUseCase>(), reject.Object);
        var response = (OkObjectResult)await controller.Reject(14, request, cancellation.Token);
        ((ApiResponse<MenuSubmissionDto>)response.Value!).Should().BeEquivalentTo(new { Success = true, Data = result });
        reject.Verify(x => x.ExecuteAsync(14, request, cancellation.Token), Times.Once);
    }

    // TEST-05: Expose only the four documented routes with MOD and ADMIN role protection.
    /// <summary>Verifies attribute routing and authorization remain aligned with the API Design.</summary>
    [Fact]
    public void Routes_ShouldMatchDesign()
    {
        var type = typeof(MenuSubmissionsController);
        type.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/v1/mod/menu-submissions");
        type.GetCustomAttribute<AuthorizeAttribute>()!.Roles.Should().Be("MOD,ADMIN");
        type.GetMethod("List")!.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().BeNull();
        type.GetMethod("Detail")!.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("{submissionId:long}");
        type.GetMethod("Approve")!.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("{submissionId:long}/approve");
        type.GetMethod("Reject")!.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("{submissionId:long}/reject");
    }
}
