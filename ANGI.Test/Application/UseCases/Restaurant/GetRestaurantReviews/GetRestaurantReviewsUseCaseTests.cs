using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.GetRestaurantReviews;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.GetRestaurantReviews;

public sealed class GetRestaurantReviewsUseCaseTests
{
    private readonly Mock<IRestaurantRepository> _repositoryMock = new();
    private readonly GetRestaurantReviewsRequestDtoValidator _validator = new();

    private GetRestaurantReviewsUseCase CreateUseCase() =>
        new(_repositoryMock.Object, _validator);

    // TEST-01: Happy Path - Paginated reviews and rating breakdown returned
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantExists_ShouldReturnRestaurantReviews()
    {
        var restaurantId = 7L;
        var request = new GetRestaurantReviewsRequestDto
        {
            RestaurantId = restaurantId,
            Page = 1,
            PageSize = 20
        };

        var reviews = new List<RestaurantReview>
        {
            new()
            {
                Id = 640,
                RestaurantId = restaurantId,
                Rating = 5,
                Content = "Nước dùng đậm đà",
                Status = ContentStatus.Visible,
                CreatedAt = new DateTime(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc),
                User = new User
                {
                    Id = 12,
                    DisplayName = "Nguyễn An"
                },
                Reply = new ReviewReply
                {
                    Content = "Cảm ơn bạn đã ghé quán!",
                    Status = ContentStatus.Visible,
                    CreatedAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc)
                }
            }
        };

        var breakdown = new Dictionary<string, int>
        {
            ["1"] = 3,
            ["2"] = 5,
            ["3"] = 12,
            ["4"] = 60,
            ["5"] = 133
        };

        _repositoryMock
            .Setup(r => r.ExistsAndActiveAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _repositoryMock
            .Setup(r => r.GetReviewsAsync(restaurantId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((reviews, 57L, breakdown, 4.6m, 213));

        var useCase = CreateUseCase();
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(57);
        result.TotalPages.Should().Be(3);
        result.RatingAvg.Should().Be(4.6m);
        result.RatingCount.Should().Be(213);
        result.RatingBreakdown.Should().ContainKey("5").WhoseValue.Should().Be(133);
        result.Items.Should().HaveCount(1);
        result.Items[0].Id.Should().Be(640);
        result.Items[0].User.DisplayName.Should().Be("Nguyễn An");
        result.Items[0].Reply.Should().NotBeNull();
        result.Items[0].Reply!.Content.Should().Be("Cảm ơn bạn đã ghé quán!");
    }

    // TEST-02: Exception Path - Restaurant does not exist, throw 404 RESTAURANT_NOT_FOUND
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantNotFound_ShouldThrowNotFoundException()
    {
        var restaurantId = 999L;
        var request = new GetRestaurantReviewsRequestDto { RestaurantId = restaurantId };

        _repositoryMock
            .Setup(r => r.ExistsAndActiveAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var useCase = CreateUseCase();
        var act = async () => await useCase.ExecuteAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }
}
