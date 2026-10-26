using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.CreateRestaurantReview;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.CreateRestaurantReview;

public sealed class CreateRestaurantReviewUseCaseTests
{
    private readonly Mock<IRestaurantRepository> _repositoryMock = new();
    private readonly Mock<ICurrentUserService> _currentUserServiceMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IUnitOfWorkTransaction> _transactionMock = new();
    private readonly CreateRestaurantReviewRequestDtoValidator _validator = new();

    public CreateRestaurantReviewUseCaseTests()
    {
        _unitOfWorkMock
            .Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_transactionMock.Object);
    }

    private CreateRestaurantReviewUseCase CreateUseCase() =>
        new(
            _repositoryMock.Object,
            _currentUserServiceMock.Object,
            _unitOfWorkMock.Object,
            _validator);

    // TEST-01: Happy Path - Submits review successfully, recalculates restaurant rating metrics, notifies owner
    [Fact]
    public async Task ExecuteAsync_WhenValidRequest_ShouldCreateReviewAndRecalculateRating()
    {
        const int userId = 12;
        const long restaurantId = 7L;
        var request = new CreateRestaurantReviewRequestDto
        {
            Rating = 5,
            Content = "Nước dùng đậm đà, giá hợp lý",
            ImageMediaIds = new[] { 9201L },
            RoadmapItemId = 1502L
        };

        var reviewer = new User
        {
            Id = userId,
            DisplayName = "Nguyễn An",
            AvatarMedia = new MediaFile { Id = 101, Url = "https://cdn.angi.vn/avatars/12.jpg" }
        };

        var restaurant = new Domain.Entities.Restaurant
        {
            Id = restaurantId,
            OwnerId = 99,
            Name = "Bún Bò Huế O Phượng",
            RatingAvg = 4.0m,
            RatingCount = 1
        };

        var mediaFiles = new List<MediaFile>
        {
            new() { Id = 9201L, UploaderId = userId, Url = "https://cdn.angi.vn/reviews/640-1.jpg" }
        };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(userId);
        _repositoryMock.Setup(r => r.GetReviewerUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(reviewer);
        _repositoryMock.Setup(r => r.LockRestaurantForReviewAsync(restaurantId, It.IsAny<CancellationToken>())).ReturnsAsync(restaurant);
        _repositoryMock.Setup(r => r.GetVisibleRatingStatsAsync(restaurantId, It.IsAny<CancellationToken>())).ReturnsAsync((1, 4));
        _repositoryMock.Setup(r => r.HasUserReviewedRestaurantAsync(restaurantId, userId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _repositoryMock.Setup(r => r.IsRoadmapItemValidForRestaurantAsync(userId, 1502L, restaurantId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _repositoryMock.Setup(r => r.GetUserReviewMediaFilesAsync(userId, It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>())).ReturnsAsync(mediaFiles);

        RestaurantReview? savedReview = null;
        _repositoryMock
            .Setup(r => r.AddReview(It.IsAny<RestaurantReview>()))
            .Callback<RestaurantReview>(r => savedReview = r);

        Domain.Entities.Notification? savedNotification = null;
        _repositoryMock
            .Setup(r => r.AddNotification(It.IsAny<Domain.Entities.Notification>()))
            .Callback<Domain.Entities.Notification>(n => savedNotification = n);

        var useCase = CreateUseCase();
        var result = await useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        result.Should().NotBeNull();
        result.RestaurantId.Should().Be(restaurantId);
        result.Rating.Should().Be(5);
        result.Content.Should().Be("Nước dùng đậm đà, giá hợp lý");
        result.User.DisplayName.Should().Be("Nguyễn An");
        result.User.AvatarUrl.Should().Be("https://cdn.angi.vn/avatars/12.jpg");
        result.Images.Should().ContainSingle().Which.Should().Be("https://cdn.angi.vn/reviews/640-1.jpg");
        result.RoadmapItemId.Should().Be(1502L);

        // Safe rating recalculation assertion: (4.0 * 1 + 5) / 2 = 4.5
        restaurant.RatingCount.Should().Be(2);
        restaurant.RatingAvg.Should().Be(4.5m);

        savedReview.Should().NotBeNull();
        savedReview!.Rating.Should().Be(5);
        savedReview.Images.Should().HaveCount(1);

        savedNotification.Should().NotBeNull();
        savedNotification!.RecipientId.Should().Be(99);
        savedNotification.Type.Should().Be("RESTAURANT_REVIEWED");

        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _transactionMock.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Exception Path 1 - Rating outside [1, 5] throws ValidationException (400)
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task ExecuteAsync_WhenRatingOutOfRange_ShouldThrowValidationException(short invalidRating)
    {
        var request = new CreateRestaurantReviewRequestDto
        {
            Rating = invalidRating,
            Content = "Review text"
        };

        var useCase = CreateUseCase();
        var act = () => useCase.ExecuteAsync(7L, request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-03: Exception Path 2 - RestaurantId does not exist throws NotFoundException (404 RESTAURANT_NOT_FOUND)
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantNotFound_ShouldThrowNotFoundException()
    {
        const int userId = 12;
        const long restaurantId = 9999L;
        var request = new CreateRestaurantReviewRequestDto { Rating = 5 };

        var reviewer = new User { Id = userId, DisplayName = "Nguyễn An" };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(userId);
        _repositoryMock.Setup(r => r.GetReviewerUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(reviewer);
        _repositoryMock.Setup(r => r.LockRestaurantForReviewAsync(restaurantId, It.IsAny<CancellationToken>())).ReturnsAsync((Domain.Entities.Restaurant?)null);

        var useCase = CreateUseCase();
        var act = () => useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    // TEST-04: Exception Path 3 - User has already reviewed restaurant throws ConflictException (409 REVIEW_ALREADY_EXISTS)
    [Fact]
    public async Task ExecuteAsync_WhenUserAlreadyReviewed_ShouldThrowConflictException()
    {
        const int userId = 12;
        const long restaurantId = 7L;
        var request = new CreateRestaurantReviewRequestDto { Rating = 5 };

        var reviewer = new User { Id = userId, DisplayName = "Nguyễn An" };
        var restaurant = new Domain.Entities.Restaurant { Id = restaurantId };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(userId);
        _repositoryMock.Setup(r => r.GetReviewerUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(reviewer);
        _repositoryMock.Setup(r => r.LockRestaurantForReviewAsync(restaurantId, It.IsAny<CancellationToken>())).ReturnsAsync(restaurant);
        _repositoryMock.Setup(r => r.HasUserReviewedRestaurantAsync(restaurantId, userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var useCase = CreateUseCase();
        var act = () => useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("REVIEW_ALREADY_EXISTS");
    }

    // TEST-05: Exception Path 4 - User not logged in throws UnauthorizedException (401 UNAUTHORIZED)
    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns((int?)null);

        var request = new CreateRestaurantReviewRequestDto { Rating = 5 };
        var useCase = CreateUseCase();
        var act = () => useCase.ExecuteAsync(7L, request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-06: Exception Path 5 - Roadmap item is invalid throws BadRequestException (400 VALIDATION_FAILED)
    [Fact]
    public async Task ExecuteAsync_WhenRoadmapItemInvalid_ShouldThrowBadRequestException()
    {
        const int userId = 12;
        const long restaurantId = 7L;
        var request = new CreateRestaurantReviewRequestDto
        {
            Rating = 5,
            RoadmapItemId = 999L
        };

        var reviewer = new User { Id = userId, DisplayName = "Nguyễn An" };
        var restaurant = new Domain.Entities.Restaurant { Id = restaurantId };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(userId);
        _repositoryMock.Setup(r => r.GetReviewerUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(reviewer);
        _repositoryMock.Setup(r => r.LockRestaurantForReviewAsync(restaurantId, It.IsAny<CancellationToken>())).ReturnsAsync(restaurant);
        _repositoryMock.Setup(r => r.HasUserReviewedRestaurantAsync(restaurantId, userId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _repositoryMock.Setup(r => r.IsRoadmapItemValidForRestaurantAsync(userId, 999L, restaurantId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var useCase = CreateUseCase();
        var act = () => useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("VALIDATION_FAILED");
    }

    // TEST-07: Exception Path 6 - Uploaded image does not belong to user throws BadRequestException (400 VALIDATION_FAILED)
    [Fact]
    public async Task ExecuteAsync_WhenImageDoesNotBelongToUser_ShouldThrowBadRequestException()
    {
        const int userId = 12;
        const long restaurantId = 7L;
        var request = new CreateRestaurantReviewRequestDto
        {
            Rating = 5,
            ImageMediaIds = new[] { 9999L }
        };

        var reviewer = new User { Id = userId, DisplayName = "Nguyễn An" };
        var restaurant = new Domain.Entities.Restaurant { Id = restaurantId };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(userId);
        _repositoryMock.Setup(r => r.GetReviewerUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(reviewer);
        _repositoryMock.Setup(r => r.LockRestaurantForReviewAsync(restaurantId, It.IsAny<CancellationToken>())).ReturnsAsync(restaurant);
        _repositoryMock.Setup(r => r.HasUserReviewedRestaurantAsync(restaurantId, userId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _repositoryMock.Setup(r => r.GetUserReviewMediaFilesAsync(userId, It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MediaFile>());

        var useCase = CreateUseCase();
        var act = () => useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("VALIDATION_FAILED");
    }

    // The aggregate is recomputed from the visible reviews, so a stale or rounded stored value never carries over.
    [Fact]
    public async Task ExecuteAsync_ShouldRecomputeRatingFromVisibleReviews()
    {
        var restaurant = new Domain.Entities.Restaurant { Id = 7L, OwnerId = 99, Name = "Quán", RatingAvg = 3.0m, RatingCount = 5 };
        _currentUserServiceMock.Setup(s => s.UserId).Returns(12);
        _repositoryMock.Setup(r => r.GetReviewerUserAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = 12, DisplayName = "An" });
        _repositoryMock.Setup(r => r.LockRestaurantForReviewAsync(7L, It.IsAny<CancellationToken>())).ReturnsAsync(restaurant);
        _repositoryMock.Setup(r => r.GetVisibleRatingStatsAsync(7L, It.IsAny<CancellationToken>())).ReturnsAsync((2, 10));

        await CreateUseCase().ExecuteAsync(7L, new CreateRestaurantReviewRequestDto { Rating = 5 }, CancellationToken.None);

        (restaurant.RatingCount, restaurant.RatingAvg).Should().Be((3, 5.00m));
    }
}
