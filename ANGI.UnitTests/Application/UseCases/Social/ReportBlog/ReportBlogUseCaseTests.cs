using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Social.ReportBlog;
using ANGI.Application.UseCases.Validators.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.ReportBlog;

/// <summary>Unit tests for REP-02 ReportBlogUseCase.</summary>
public sealed class ReportBlogUseCaseTests
{
    private readonly IValidator<ReportBlogRequestDto> _validator = new ReportBlogRequestDtoValidator();
    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<IReportRepository> _reportRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public ReportBlogUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(12);
    }

    private ReportBlogUseCase CreateUseCase() =>
        new(_validator, _blogRepository.Object, _reportRepository.Object, _currentUserService.Object, _unitOfWork.Object);

    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldCreateReport()
    {
        // Arrange
        const long blogId = 45;
        const int userId = 12;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 99, // different from reporter
            Title = "Bài viết vi phạm",
            Status = BlogStatus.Published
        };
        var request = new ReportBlogRequestDto
        {
            ReasonCode = "spam",
            Description = "Bài viết quảng cáo",
            EvidenceMediaIds = [9401]
        };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        _reportRepository.Setup(r => r.HasPendingReportAsync(userId, ReportTargetType.Blog, blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Report? addedReport = null;
        _reportRepository.Setup(r => r.Add(It.IsAny<Report>()))
            .Callback<Report>(r =>
            {
                r.Id = 88;
                addedReport = r;
            });

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.ReportId.Should().Be(88);
        result.Status.Should().Be("pending");

        addedReport.Should().NotBeNull();
        addedReport!.ReporterId.Should().Be(userId);
        addedReport.TargetType.Should().Be(ReportTargetType.Blog);
        addedReport.BlogId.Should().Be(blogId);
        addedReport.ReasonCode.Should().Be(ReportReasonCode.Spam);
        addedReport.Status.Should().Be(ReportStatus.Pending);
        addedReport.Evidence.Should().Contain("9401");

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReportingOwnContent_ShouldThrowBadRequestException()
    {
        // Arrange
        const long blogId = 45;
        const int userId = 12;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = userId, // Same as reporter
            Title = "Bài viết của tôi",
            Status = BlogStatus.Published
        };
        var request = new ReportBlogRequestDto { ReasonCode = "spam" };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<BadRequestException>();
        ex.Which.ErrorCode.Should().Be("CANNOT_REPORT_OWN_CONTENT");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReportAlreadyPending_ShouldThrowConflictException()
    {
        // Arrange
        const long blogId = 45;
        const int userId = 12;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 99,
            Title = "Bài viết",
            Status = BlogStatus.Published
        };
        var request = new ReportBlogRequestDto { ReasonCode = "spam" };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        _reportRepository.Setup(r => r.HasPendingReportAsync(userId, ReportTargetType.Blog, blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.Which.ErrorCode.Should().Be("REPORT_ALREADY_PENDING");
    }

    [Fact]
    public async Task ExecuteAsync_WhenBlogDoesNotExist_ShouldThrowNotFoundException()
    {
        // Arrange
        const long nonExistentBlogId = 9999;
        var request = new ReportBlogRequestDto { ReasonCode = "spam" };

        _blogRepository.Setup(r => r.GetByIdAsync(nonExistentBlogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Blog?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(nonExistentBlogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReasonIsOtherWithoutDescription_ShouldThrowValidationException()
    {
        // Arrange
        var request = new ReportBlogRequestDto
        {
            ReasonCode = "other",
            Description = "" // missing
        };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(45, request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }
}
