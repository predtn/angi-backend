using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.CreateRestaurantReport;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.CreateRestaurantReport;

/// <summary>Unit tests for REP-01 CreateRestaurantReportUseCase.</summary>
public sealed class CreateRestaurantReportUseCaseTests
{
    private readonly IValidator<CreateRestaurantReportRequestDto> _validator = new CreateRestaurantReportRequestDtoValidator();
    private readonly Mock<IRestaurantRepository> _restaurantRepository = new();
    private readonly Mock<IReportRepository> _reportRepository = new();
    private readonly Mock<IMediaRepository> _mediaRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public CreateRestaurantReportUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(12);
        // By default every evidence id is one of the reporter's own uploads.
        _mediaRepository.Setup(r => r.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, IReadOnlyCollection<long> ids, CancellationToken _) =>
                ids.Select(id => new MediaFile { Id = id }).ToArray());
    }

    private CreateRestaurantReportUseCase CreateUseCase() =>
        new(_validator, _restaurantRepository.Object, _reportRepository.Object, _mediaRepository.Object, _currentUserService.Object, _unitOfWork.Object);

    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldCreateReportSuccessfully()
    {
        // Arrange
        const long restaurantId = 100;
        const int userId = 12;
        var request = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "hygiene",
            Description = "Bát đũa không sạch",
            EvidenceMediaIds = [9301]
        };

        _restaurantRepository.Setup(r => r.ExistsAndActiveAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportRepository.Setup(r => r.HasPendingReportAsync(userId, ReportTargetType.Restaurant, restaurantId, It.IsAny<CancellationToken>()))
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
        var result = await useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.ReportId.Should().Be(88);
        result.Status.Should().Be("pending");

        addedReport.Should().NotBeNull();
        addedReport!.ReporterId.Should().Be(userId);
        addedReport.TargetType.Should().Be(ReportTargetType.Restaurant);
        addedReport.RestaurantId.Should().Be(restaurantId);
        addedReport.BlogId.Should().BeNull();
        addedReport.ReasonCode.Should().Be(ReportReasonCode.Hygiene);
        addedReport.Description.Should().Be("Bát đũa không sạch");
        addedReport.Status.Should().Be(ReportStatus.Pending);
        addedReport.Evidence.Should().Contain("9301");

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRestaurantDoesNotExist_ShouldThrowNotFoundException()
    {
        // Arrange
        const long restaurantId = 999;
        var request = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "wrong_info",
            Description = "Sai địa chỉ"
        };

        _restaurantRepository.Setup(r => r.ExistsAndActiveAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
        ex.Which.Message.Should().Be("Không tìm thấy nhà hàng.");

        _reportRepository.Verify(r => r.Add(It.IsAny<Report>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUserHasPendingReport_ShouldThrowConflictException()
    {
        // Arrange
        const long restaurantId = 100;
        const int userId = 12;
        var request = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "closed",
            Description = "Quán đã đóng cửa vĩnh viễn"
        };

        _restaurantRepository.Setup(r => r.ExistsAndActiveAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportRepository.Setup(r => r.HasPendingReportAsync(userId, ReportTargetType.Restaurant, restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.Which.ErrorCode.Should().Be("REPORT_ALREADY_PENDING");
        ex.Which.Message.Should().Be("Bạn đã có báo cáo về nhà hàng này đang chờ xử lý.");

        _reportRepository.Verify(r => r.Add(It.IsAny<Report>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        const long restaurantId = 100;
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);
        var request = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "spam"
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        ex.Which.Message.Should().Be("Bạn cần đăng nhập để báo cáo nhà hàng.");
    }

    [Theory]
    [InlineData("wrong_info", ReportReasonCode.WrongInfo)]
    [InlineData("closed", ReportReasonCode.Closed)]
    [InlineData("spam", ReportReasonCode.Spam)]
    [InlineData("offensive", ReportReasonCode.Offensive)]
    [InlineData("hygiene", ReportReasonCode.Hygiene)]
    [InlineData("scam", ReportReasonCode.Scam)]
    [InlineData("other", ReportReasonCode.Other)]
    public async Task ExecuteAsync_WithAllValidReasonCodes_ShouldMapCorrectly(string reasonCode, ReportReasonCode expectedEnum)
    {
        // Arrange
        const long restaurantId = 100;
        var request = new CreateRestaurantReportRequestDto
        {
            ReasonCode = reasonCode,
            Description = "Chi tiết báo cáo"
        };

        _restaurantRepository.Setup(r => r.ExistsAndActiveAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportRepository.Setup(r => r.HasPendingReportAsync(It.IsAny<int>(), ReportTargetType.Restaurant, restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Report? addedReport = null;
        _reportRepository.Setup(r => r.Add(It.IsAny<Report>()))
            .Callback<Report>(r =>
            {
                r.Id = 1;
                addedReport = r;
            });

        var useCase = CreateUseCase();

        // Act
        await useCase.ExecuteAsync(restaurantId, request, CancellationToken.None);

        // Assert
        addedReport.Should().NotBeNull();
        addedReport!.ReasonCode.Should().Be(expectedEnum);
    }

    [Fact]
    public async Task ExecuteAsync_WithEvidenceNotUploadedByReporter_ShouldThrowValidationWithoutReport()
    {
        _restaurantRepository.Setup(r => r.ExistsAndActiveAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mediaRepository.Setup(r => r.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile { Id = 9301 }]);
        var request = new CreateRestaurantReportRequestDto { ReasonCode = "scam", EvidenceMediaIds = [9301, 7777] };

        var act = () => CreateUseCase().ExecuteAsync(100, request, CancellationToken.None);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("VALIDATION_FAILED");
        _reportRepository.Verify(r => r.Add(It.IsAny<Report>()), Times.Never);
    }
}
