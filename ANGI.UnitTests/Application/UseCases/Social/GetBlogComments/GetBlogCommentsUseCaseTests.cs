using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Social.GetBlogComments;
using ANGI.Application.UseCases.Validators.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.GetBlogComments;

/// <summary>Unit tests for BLOG-03 (<see cref="GetBlogCommentsUseCase"/>).</summary>
public sealed class GetBlogCommentsUseCaseTests
{
    private readonly IValidator<GetBlogCommentsRequestDto> _validator = new GetBlogCommentsRequestDtoValidator();
    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();

    private GetBlogCommentsUseCase CreateUseCase() =>
        new(_validator, _blogRepository.Object, _currentUserService.Object);

    // Happy Path 1: Lấy danh sách bình luận theo phân trang thành công
    // TEST-01: Verify ExecuteAsync when blog exists; should return paginated comments.
    /// <summary>Verify ExecuteAsync when blog exists; should return paginated comments.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenBlogExists_ShouldReturnPaginatedComments()
    {
        // Arrange
        const long blogId = 45;
        const int currentUserId = 12;
        _currentUserService.Setup(s => s.UserId).Returns(currentUserId);

        var request = new GetBlogCommentsRequestDto { Page = 1, PageSize = 20 };

        var comments = new List<BlogComment>
        {
            new()
            {
                Id = 302,
                BlogId = blogId,
                UserId = 12,
                Content = "Quán này ngon thật!",
                Status = ContentStatus.Visible,
                CreatedAt = new DateTime(2026, 10, 13, 11, 0, 0, DateTimeKind.Utc),
                User = new User
                {
                    Id = 12,
                    DisplayName = "Nguyễn An",
                    AvatarMedia = new MediaFile { Id = 800, Url = "https://cdn.angi.vn/avatars/12.jpg" }
                }
            },
            new()
            {
                Id = 303,
                BlogId = blogId,
                UserId = 15,
                Content = "Cảm ơn bạn đã chia sẻ!",
                Status = ContentStatus.Visible,
                CreatedAt = new DateTime(2026, 10, 13, 11, 5, 0, DateTimeKind.Utc),
                User = new User
                {
                    Id = 15,
                    DisplayName = "Trần Bình",
                    AvatarMedia = null
                }
            }
        };

        _blogRepository
            .Setup(r => r.ExistsPublishedAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _blogRepository
            .Setup(r => r.GetVisibleCommentsAsync(blogId, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((comments, 57L));

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(57L);
        result.TotalPages.Should().Be(3);
        result.Items.Should().HaveCount(2);

        result.Items[0].Id.Should().Be(302);
        result.Items[0].BlogId.Should().Be(blogId);
        result.Items[0].User.Id.Should().Be(12);
        result.Items[0].User.DisplayName.Should().Be("Nguyễn An");
        result.Items[0].User.AvatarUrl.Should().Be("https://cdn.angi.vn/avatars/12.jpg");
        result.Items[0].Content.Should().Be("Quán này ngon thật!");
        result.Items[0].IsMine.Should().BeTrue();

        result.Items[1].Id.Should().Be(303);
        result.Items[1].IsMine.Should().BeFalse();
    }

    // Exception Path 1: Bắn lỗi 404 BLOG_NOT_FOUND khi blogId không tồn tại
    // TEST-02: Verify ExecuteAsync when blog does not exist; should throw not found exception.
    /// <summary>Verify ExecuteAsync when blog does not exist; should throw not found exception.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenBlogDoesNotExist_ShouldThrowNotFoundException()
    {
        // Arrange
        const long blogId = 999;
        var request = new GetBlogCommentsRequestDto { Page = 1, PageSize = 20 };

        _blogRepository
            .Setup(r => r.ExistsPublishedAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }

    // Exception Path 2: Tham số phân trang không hợp lệ
    // TEST-03: Verify ExecuteAsync when page is invalid; should throw validation exception.
    /// <summary>Verify ExecuteAsync when page is invalid; should throw validation exception.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenPageIsInvalid_ShouldThrowValidationException()
    {
        // Arrange
        var request = new GetBlogCommentsRequestDto { Page = 0, PageSize = 20 };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(45, request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*page*");
    }
}
