using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Restaurant;

public sealed class OwnerRestaurantReviewsControllerTests
{
    private readonly Mock<IGetOwnerReviewsUseCase> _getOwnerReviewsMock = new();
    private readonly Mock<IReplyReviewUseCase> _replyReviewMock = new();

    private OwnerRestaurantReviewsController CreateController() =>
        new(_getOwnerReviewsMock.Object, _replyReviewMock.Object);

    // TEST-01: GetReviews (OWN-19) returns 200 OK with Paged<ReviewDto> wrapped in ApiResponse
    [Fact]
    public async Task GetReviews_WithValidQuery_ShouldReturnOkWithPagedReviews()
    {
        // Arrange
        var request = new GetOwnerReviewsRequestDto { Page = 1, PageSize = 20 };
        var pagedData = new Paged<ReviewDto>
        {
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1,
            Items = new List<ReviewDto>
            {
                new() { Id = 100L, Content = "Quán rất ngon", Rating = 5 }
            }
        };

        _getOwnerReviewsMock
            .Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedData);

        var controller = CreateController();

        // Act
        var actionResult = await controller.GetReviews(request, CancellationToken.None);

        // Assert
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = okResult.Value.Should().BeOfType<ApiResponse<Paged<ReviewDto>>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(pagedData);

        _getOwnerReviewsMock.Verify(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reply (OWN-20) returns 200 OK with ReviewReplyDto wrapped in ApiResponse
    [Fact]
    public async Task Reply_WithValidRequest_ShouldReturnOkWithReviewReply()
    {
        // Arrange
        const long reviewId = 100L;
        var request = new ReplyReviewRequestDto { Content = "Cảm ơn bạn đã đánh giá tốt!" };
        var replyDto = new ReviewReplyDto
        {
            Content = request.Content,
            Status = "visible",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _replyReviewMock
            .Setup(x => x.ExecuteAsync(reviewId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(replyDto);

        var controller = CreateController();

        // Act
        var actionResult = await controller.Reply(reviewId, request, CancellationToken.None);

        // Assert
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = okResult.Value.Should().BeOfType<ApiResponse<ReviewReplyDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(replyDto);

        _replyReviewMock.Verify(x => x.ExecuteAsync(reviewId, request, It.IsAny<CancellationToken>()), Times.Once);
    }
}
