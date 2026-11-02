using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.GetDashboardSummary;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetDashboardSummary;

public sealed class GetDashboardSummaryUseCaseTests
{
    private readonly Mock<IAdministrationDashboardRepository> _repositoryMock;
    private readonly FixedTimeProvider _timeProvider;
    private readonly GetDashboardSummaryRequestDtoValidator _validator;
    private readonly GetDashboardSummaryUseCase _useCase;

    public GetDashboardSummaryUseCaseTests()
    {
        _repositoryMock = new Mock<IAdministrationDashboardRepository>();
        _timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        _validator = new GetDashboardSummaryRequestDtoValidator();
        _useCase = new GetDashboardSummaryUseCase(_validator, _repositoryMock.Object, _timeProvider);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WithCustomDates_ShouldReturnRepositorySummary()
    {
        // Arrange
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);
        var request = new GetDashboardSummaryRequestDto { From = from, To = to };

        var expectedDto = new DashboardSummaryDto
        {
            From = from,
            To = to,
            Users = new DashboardUsersSummaryDto
            {
                Total = 1250,
                New = 340,
                ByRole = new Dictionary<string, int> { ["TRAVELER"] = 1180, ["ADMIN"] = 2 },
                ByStatus = new Dictionary<string, int> { ["active"] = 1190 }
            },
            Restaurants = new DashboardRestaurantsSummaryDto
            {
                Total = 62,
                New = 15,
                ByVerificationStatus = new Dictionary<string, int> { ["verified"] = 50 },
                ByModerationStatus = new Dictionary<string, int> { ["visible"] = 58 }
            },
            Queues = new DashboardQueuesSummaryDto
            {
                PendingVerifications = 4,
                PendingMenus = 6,
                PendingRestaurantReports = 3,
                PendingSocialReports = 7
            },
            Content = new DashboardContentSummaryDto { Blogs = 85, Comments = 410, Reviews = 260 },
            Roadmaps = new DashboardRoadmapsSummaryDto { Created = 520, AiGenerationJobs = 730 },
            Feedback = new DashboardFeedbackSummaryDto { Likes = 3100, Dislikes = 420 },
            Outbox = new DashboardOutboxSummaryDto { Pending = 2, Dead = 0 }
        };

        _repositoryMock
            .Setup(repo => repo.GetSummaryAsync(from, to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDto);

        // Act
        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.From.Should().Be(from);
        result.To.Should().Be(to);
        result.Users.Total.Should().Be(1250);
        result.Users.New.Should().Be(340);
        result.Restaurants.Total.Should().Be(62);
        result.Queues.PendingVerifications.Should().Be(4);
        result.Content.Blogs.Should().Be(85);
        result.Feedback.Likes.Should().Be(3100);
        result.Outbox.Pending.Should().Be(2);
        _repositoryMock.Verify(repo => repo.GetSummaryAsync(from, to, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WithoutDates_ShouldDefaultToPast30Days()
    {
        // Arrange
        var request = new GetDashboardSummaryRequestDto();
        var expectedTo = new DateOnly(2026, 10, 2);
        var expectedFrom = expectedTo.AddDays(-30);

        _repositoryMock
            .Setup(repo => repo.GetSummaryAsync(expectedFrom, expectedTo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummaryDto { From = expectedFrom, To = expectedTo });

        // Act
        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.From.Should().Be(expectedFrom);
        result.To.Should().Be(expectedTo);
        _repositoryMock.Verify(repo => repo.GetSummaryAsync(expectedFrom, expectedTo, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_StartDateAfterEndDate_ShouldThrowValidationOrBadRequestException()
    {
        // Arrange: StartDate (From) > EndDate (To)
        var request = new GetDashboardSummaryRequestDto
        {
            StartDate = new DateOnly(2026, 10, 10),
            EndDate = new DateOnly(2026, 10, 1)
        };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>()
            .Where(e => e is ValidationException || e is BadRequestException);

        _repositoryMock.Verify(repo => repo.GetSummaryAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_DateRangeExceeds366Days_ShouldThrowValidationException()
    {
        // Arrange: Range > 366 days
        var request = new GetDashboardSummaryRequestDto
        {
            From = new DateOnly(2024, 1, 1),
            To = new DateOnly(2026, 1, 1)
        };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();

        _repositoryMock.Verify(repo => repo.GetSummaryAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
