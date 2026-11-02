using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.GetDashboardTimeSeries;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetDashboardTimeSeries;

public sealed class GetDashboardTimeSeriesUseCaseTests
{
    private readonly Mock<IAdministrationDashboardRepository> _repositoryMock;
    private readonly GetDashboardTimeSeriesRequestDtoValidator _validator;
    private readonly GetDashboardTimeSeriesUseCase _useCase;

    public GetDashboardTimeSeriesUseCaseTests()
    {
        _repositoryMock = new Mock<IAdministrationDashboardRepository>();
        _validator = new GetDashboardTimeSeriesRequestDtoValidator();
        _useCase = new GetDashboardTimeSeriesUseCase(_validator, _repositoryMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldReturnPointsFromRepository()
    {
        // Arrange
        var from = new DateOnly(2026, 10, 1);
        var to = new DateOnly(2026, 10, 5);
        var request = new GetDashboardTimeSeriesRequestDto
        {
            Metric = "new_users",
            From = from,
            To = to,
            Interval = "day"
        };

        var expectedPoints = new List<DashboardTimeSeriesPointDto>
        {
            new() { Bucket = new DateOnly(2026, 10, 1), Value = 18 },
            new() { Bucket = new DateOnly(2026, 10, 2), Value = 25 },
            new() { Bucket = new DateOnly(2026, 10, 3), Value = 0 }
        };

        _repositoryMock
            .Setup(repo => repo.GetTimeSeriesAsync("new_users", from, to, "day", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPoints);

        // Act
        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Metric.Should().Be("new_users");
        result.Interval.Should().Be("day");
        result.Points.Should().HaveCount(3);
        result.Points[0].Value.Should().Be(18);
        _repositoryMock.Verify(repo => repo.GetTimeSeriesAsync("new_users", from, to, "day", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_StartDateAfterEndDate_ShouldThrowValidationOrBadRequestException()
    {
        // Arrange
        var request = new GetDashboardTimeSeriesRequestDto
        {
            Metric = "new_users",
            StartDate = new DateOnly(2026, 10, 10),
            EndDate = new DateOnly(2026, 10, 1)
        };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>()
            .Where(e => e is ValidationException || e is BadRequestException);

        _repositoryMock.Verify(repo => repo.GetTimeSeriesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_RangeOver366DaysOnDayInterval_ShouldThrowValidationException()
    {
        // Arrange
        var request = new GetDashboardTimeSeriesRequestDto
        {
            Metric = "new_users",
            From = new DateOnly(2024, 1, 1),
            To = new DateOnly(2025, 2, 1),
            Interval = "day"
        };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();

        _repositoryMock.Verify(repo => repo.GetTimeSeriesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_InvalidMetric_ShouldThrowValidationException()
    {
        // Arrange
        var request = new GetDashboardTimeSeriesRequestDto
        {
            Metric = "invalid_metric",
            From = new DateOnly(2026, 10, 1),
            To = new DateOnly(2026, 10, 5)
        };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();

        _repositoryMock.Verify(repo => repo.GetTimeSeriesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
