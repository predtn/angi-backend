using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.GetOwnerReviews;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.GetOwnerReviews;

public sealed class GetOwnerReviewsUseCaseTests
{
    private readonly Mock<IRestaurantRepository> _repositoryMock = new();
    private readonly Mock<ICurrentUserService> _currentUserServiceMock = new();
    private readonly GetOwnerReviewsRequestDtoValidator _validator = new();

    private GetOwnerReviewsUseCase CreateUseCase() =>
        new(_repositoryMock.Object, _currentUserServiceMock.Object, _validator);

    // TEST-01: Happy Path - Paginated owner reviews returned successfully
    [Fact]
    public async Task ExecuteAsync_WhenOwnerHasRestaurant_ShouldReturnPagedReviews()
    {
        // Arrange
        const int ownerId = 42;
        const long restaurantId = 100L;
        var request = new GetOwnerReviewsRequestDto
        {
            Page = 1,
            PageSize = 10,
            Rating = 5,
            Replied = true
        };

        var restaurant = new ANGI.Domain.Entities.Restaurant
        {
            Id = restaurantId,
            OwnerId = ownerId,
            Name = "Quán Cơm Tấm Sài Gòn"
        };

        var reviews = new List<RestaurantReview>
        {
            new()
            {
                Id = 1L,
                RestaurantId = restaurantId,
                Rating = 5,
                Content = "Rất ngon và sạch sẽ",
                Status = ContentStatus.Visible,
                CreatedAt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
                User = new User
                {
                    Id = 200,
                    DisplayName = "Nguyễn Văn A"
                },
                Reply = new ReviewReply
                {
                    ReviewId = 1L,
                    ReplierId = ownerId,
                    Content = "Cảm ơn bạn đã ủng hộ!",
                    Status = ContentStatus.Visible,
                    CreatedAt = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc)
                }
            }
        };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(ownerId);
        _repositoryMock
            .Setup(r => r.GetByOwnerIdAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        _repositoryMock
            .Setup(r => r.GetOwnerReviewsAsync(restaurantId, true, 5, 1, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((reviews, 1L));

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(10);
        result.TotalItems.Should().Be(1);
        result.TotalPages.Should().Be(1);
        result.Items.Should().HaveCount(1);

        var first = result.Items[0];
        first.Id.Should().Be(1L);
        first.Rating.Should().Be(5);
        first.Content.Should().Be("Rất ngon và sạch sẽ");
        first.User.DisplayName.Should().Be("Nguyễn Văn A");
        first.Reply.Should().NotBeNull();
        first.Reply!.Content.Should().Be("Cảm ơn bạn đã ủng hộ!");
    }

    // TEST-02: Exception Path - Owner not authenticated
    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserServiceMock.Setup(s => s.UserId).Returns((int?)null);
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(new GetOwnerReviewsRequestDto(), CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-03: Exception Path - Owner has no restaurant
    [Fact]
    public async Task ExecuteAsync_WhenOwnerHasNoRestaurant_ShouldThrowNotFoundException()
    {
        // Arrange
        const int ownerId = 42;
        _currentUserServiceMock.Setup(s => s.UserId).Returns(ownerId);
        _repositoryMock
            .Setup(r => r.GetByOwnerIdAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ANGI.Domain.Entities.Restaurant?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(new GetOwnerReviewsRequestDto(), CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    // TEST-04: Validation failure - Invalid rating
    [Fact]
    public async Task ExecuteAsync_WhenInvalidRating_ShouldThrowValidationException()
    {
        // Arrange
        var request = new GetOwnerReviewsRequestDto { Rating = 6 };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }
}
