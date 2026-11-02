using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Moderation;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace ANGI.UnitTests.WebApi.Controllers.Moderation;

public sealed class ModReviewsControllerTests
{
    private readonly Mock<IGetModReviewsUseCase> _getReviewsMock = new();
    private readonly Mock<IUpdateReviewStatusUseCase> _updateStatusMock = new();
    private readonly Mock<IUpdateReviewReplyStatusUseCase> _updateReplyStatusMock = new();

    private ModReviewsController CreateController() => new(
        _getReviewsMock.Object,
        _updateStatusMock.Object,
        _updateReplyStatusMock.Object);

    [Fact]
    public void Controller_ShouldHaveCorrectRouteAndAuthorizeRoles()
    {
        var routeAttr = typeof(ModReviewsController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Single();
        routeAttr.Template.Should().Be("api/v1/mod/reviews");

        var authAttr = typeof(ModReviewsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        authAttr.Roles.Should().Contain("MODERATOR");
        authAttr.Roles.Should().Contain("ADMIN");
    }

    [Fact]
    public async Task List_ShouldReturnOkWithPagedModReviews()
    {
        var request = new GetModReviewsRequestDto { Page = 1, PageSize = 20 };
        var expectedData = new Paged<ModReviewDto>
        {
            Items = Array.Empty<ModReviewDto>(),
            Page = 1,
            PageSize = 20,
            TotalItems = 0,
            TotalPages = 0
        };

        _getReviewsMock.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedData);

        var controller = CreateController();
        var actionResult = await controller.List(request, CancellationToken.None);

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = okResult.Value.Should().BeOfType<ApiResponse<Paged<ModReviewDto>>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedData);
    }

    [Fact]
    public async Task UpdateStatus_ShouldReturnOkWithResult()
    {
        const long reviewId = 640;
        var request = new UpdateReviewStatusRequestDto { Status = "hidden", Reason = "Lý do ẩn" };
        var expectedData = new UpdateReviewStatusResponseDto { ReviewId = reviewId, Status = "hidden" };

        _updateStatusMock.Setup(x => x.ExecuteAsync(reviewId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedData);

        var controller = CreateController();
        var actionResult = await controller.UpdateStatus(reviewId, request, CancellationToken.None);

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = okResult.Value.Should().BeOfType<ApiResponse<UpdateReviewStatusResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedData);
    }

    [Fact]
    public async Task UpdateReplyStatus_ShouldReturnOkWithResult()
    {
        const long reviewId = 640;
        var request = new UpdateReviewReplyStatusRequestDto { Status = "hidden", Reason = "Lý do ẩn phản hồi" };
        var expectedData = new UpdateReviewReplyStatusResponseDto { ReviewId = reviewId, ReplyStatus = "hidden" };

        _updateReplyStatusMock.Setup(x => x.ExecuteAsync(reviewId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedData);

        var controller = CreateController();
        var actionResult = await controller.UpdateReplyStatus(reviewId, request, CancellationToken.None);

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = okResult.Value.Should().BeOfType<ApiResponse<UpdateReviewReplyStatusResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedData);
    }
}
