using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.GetModReviews;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;
using Xunit;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetModReviews;

public sealed class GetModReviewsUseCaseTests
{
    private readonly ModerationTestContext _context = new();
    private readonly Mock<IReviewModerationRepository> _repository = new();

    private GetModReviewsUseCase CreateUseCase() => new(
        new GetModReviewsRequestDtoValidator(),
        _context.Access,
        _repository.Object);

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsPagedModReviews()
    {
        // Arrange
        var request = new GetModReviewsRequestDto
        {
            Page = 1,
            PageSize = 10,
            Status = "visible"
        };

        var restaurant = new ANGI.Domain.Entities.Restaurant
        {
            Id = 7,
            Name = "Quán Phở Thìn",
            Ward = "Phường Bến Nghé",
            ProvinceName = "TP. Hồ Chí Minh",
            Latitude = 10.7768m,
            Longitude = 106.7008m,
            RatingAvg = 4.5m
        };

        var reviews = new List<RestaurantReview>
        {
            new()
            {
                Id = 100,
                RestaurantId = 7,
                Restaurant = restaurant,
                UserId = 5,
                User = new User { Id = 5, DisplayName = "Người dùng", AvatarMedia = null },
                Rating = 5,
                Content = "Rất ngon!",
                Status = ContentStatus.Visible,
                CreatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                Reply = new ReviewReply
                {
                    ReviewId = 100,
                    ReplierId = 12,
                    Content = "Cảm ơn quý khách!",
                    Status = ContentStatus.Visible,
                    CreatedAt = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc)
                }
            }
        };

        _repository.Setup(x => x.GetModReviewsAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((reviews, 1));

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

        var item = result.Items[0];
        item.Id.Should().Be(100);
        item.Content.Should().Be("Rất ngon!");
        item.Restaurant.Should().NotBeNull();
        item.Restaurant.Id.Should().Be(7);
        item.Restaurant.Name.Should().Be("Quán Phở Thìn");
        item.Reply.Should().NotBeNull();
        item.Reply!.Content.Should().Be("Cảm ơn quý khách!");
    }

    [Theory]
    [InlineData(0, 10, "Trang phải bắt đầu từ 1.")]
    [InlineData(1, 0, "Số phần tử mỗi trang phải từ 1 đến 100.")]
    [InlineData(1, 150, "Số phần tử mỗi trang phải từ 1 đến 100.")]
    [InlineData(1, 10, "Trạng thái kiểm duyệt không hợp lệ.", "unknown_status")]
    public async Task ExecuteAsync_ThrowsValidationException_WhenPagingOrStatusInvalid(
        int page,
        int pageSize,
        string expectedError,
        string? status = null)
    {
        // Arrange
        var request = new GetModReviewsRequestDto
        {
            Page = page,
            PageSize = pageSize,
            Status = status
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().Contain(e => e.ErrorMessage == expectedError);
    }
}
