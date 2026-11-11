using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
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

/// <summary>Unit tests for CreateReportUseCase (REP-02: POST /api/v1/blogs/{blogId}/reports).</summary>
public sealed class CreateReportUseCaseTests
{
    private readonly IValidator<ReportBlogRequestDto> _validator = new ReportBlogRequestDtoValidator();
    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<IReportRepository> _reportRepository = new();
    private readonly Mock<IMediaRepository> _mediaRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public CreateReportUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(100);
        // By default every evidence id is one of the reporter's own uploads.
        _mediaRepository.Setup(r => r.GetOwnedByIdsAsync(100, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, IReadOnlyCollection<long> ids, CancellationToken _) =>
                ids.Select(id => new MediaFile { Id = id }).ToArray());
    }

    /// <summary>Creates the use case with the real validator and the shared repository, media and unit-of-work mocks.</summary>
    private CreateReportUseCase CreateUseCase() =>
        new(_validator, _blogRepository.Object, _reportRepository.Object, _mediaRepository.Object, _currentUserService.Object, _unitOfWork.Object);

    // TEST-01: A valid report for another user's published blog is created and saved.
    /// <summary>Ensures a valid report on another user's blog is stored once.</summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldCreateReportSuccessfully()
    {
        // Arrange
        const long blogId = 10;
        const int reporterId = 100;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 200, // Khác reporterId
            Title = "Bài viết khám phá ẩm thực",
            Status = BlogStatus.Published
        };

        var request = new ReportBlogRequestDto
        {
            Reason = "spam",
            Details = "Nội dung quảng cáo spam lặp lại",
            EvidenceMediaIds = [5001, 5002]
        };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        _reportRepository.Setup(r => r.HasPendingReportAsync(reporterId, ReportTargetType.Blog, blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Report? insertedReport = null;
        _reportRepository.Setup(r => r.Add(It.IsAny<Report>()))
            .Callback<Report>(r =>
            {
                r.Id = 12345;
                insertedReport = r;
            });

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.ReportId.Should().Be(12345);
        result.Status.Should().Be("pending");

        insertedReport.Should().NotBeNull();
        insertedReport!.ReporterId.Should().Be(reporterId);
        insertedReport.TargetType.Should().Be(ReportTargetType.Blog);
        insertedReport.BlogId.Should().Be(blogId);
        insertedReport.ReasonCode.Should().Be(ReportReasonCode.Spam);
        insertedReport.Description.Should().Be("Nội dung quảng cáo spam lặp lại");
        insertedReport.Evidence.Should().Contain("5001").And.Contain("5002");
        insertedReport.Status.Should().Be(ReportStatus.Pending);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reporting your own blog throws CANNOT_REPORT_OWN_CONTENT.
    /// <summary>Prevents authors from reporting their own content.</summary>
    [Fact]
    public async Task ExecuteAsync_ExceptionPath1_WhenReportingOwnBlog_ShouldThrowCannotReportOwnContent()
    {
        // Arrange
        const long blogId = 10;
        const int currentUserId = 100;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = currentUserId, // Tự report bài của chính mình
            Title = "Bài viết của tôi",
            Status = BlogStatus.Published
        };

        var request = new ReportBlogRequestDto
        {
            ReasonCode = "offensive",
            Description = "Bài viết có ngôn từ phản cảm"
        };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<BadRequestException>();
        ex.Which.ErrorCode.Should().Be("CANNOT_REPORT_OWN_CONTENT");
        ex.Which.Message.Should().Be("Bạn không thể tự báo cáo nội dung của mình.");

        _reportRepository.Verify(r => r.Add(It.IsAny<Report>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: A reporter with a pending report on the same blog gets ConflictException.
    /// <summary>Prevents duplicate open reports from the same user.</summary>
    [Fact]
    public async Task ExecuteAsync_ExceptionPath2_WhenReportAlreadyPending_ShouldThrowConflictException()
    {
        // Arrange
        const long blogId = 10;
        const int reporterId = 100;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 200,
            Title = "Bài viết bị spam",
            Status = BlogStatus.Published
        };

        var request = new ReportBlogRequestDto
        {
            ReasonCode = "scam",
            Description = "Lừa đảo chiếm đoạt tài sản"
        };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        // Đã có report pending trước đó
        _reportRepository.Setup(r => r.HasPendingReportAsync(reporterId, ReportTargetType.Blog, blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.Which.ErrorCode.Should().Be("REPORT_ALREADY_PENDING");
        ex.Which.Message.Should().Be("Bạn đã có báo cáo về bài viết này đang chờ xử lý.");

        _reportRepository.Verify(r => r.Add(It.IsAny<Report>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: A blog that does not exist throws NotFoundException.
    /// <summary>Ensures a missing blog returns not found.</summary>
    [Fact]
    public async Task ExecuteAsync_ExceptionPath3_WhenBlogNotFound_ShouldThrowNotFoundException()
    {
        // Arrange
        const long nonExistentBlogId = 99999;
        var request = new ReportBlogRequestDto
        {
            ReasonCode = "spam"
        };

        _blogRepository.Setup(r => r.GetByIdAsync(nonExistentBlogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Blog?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(nonExistentBlogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
        ex.Which.Message.Should().Be("Không tìm thấy bài viết.");

        _reportRepository.Verify(r => r.Add(It.IsAny<Report>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: A blog that is not published is treated as not found.
    /// <summary>Ensures hidden or removed blogs look the same as missing ones.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenBlogNotPublished_ShouldThrowNotFoundException()
    {
        // Arrange
        const long blogId = 10;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 200,
            Title = "Bài viết bị ẩn",
            Status = BlogStatus.Hidden // Không ở trạng thái Published
        };

        var request = new ReportBlogRequestDto { ReasonCode = "spam" };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }

    // TEST-06: An unauthenticated caller gets UnauthorizedException.
    /// <summary>Ensures an anonymous caller cannot report a blog.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenUnauthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);

        var request = new ReportBlogRequestDto { ReasonCode = "spam" };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(10, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-07: Reason other without a description fails validation.
    /// <summary>Ensures an other-reason report explains itself.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenReasonIsOtherAndDescriptionMissing_ShouldThrowValidationException()
    {
        // Arrange
        var request = new ReportBlogRequestDto
        {
            ReasonCode = "other",
            Description = "" // bắt buộc khi other
        };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(10, request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }
}
