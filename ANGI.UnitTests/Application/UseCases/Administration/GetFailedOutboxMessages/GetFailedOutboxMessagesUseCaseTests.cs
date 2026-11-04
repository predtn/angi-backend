using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.GetFailedOutboxMessages;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetFailedOutboxMessages;

public sealed class GetFailedOutboxMessagesUseCaseTests
{
    private readonly Mock<IAdministrationOutboxRepository> _repositoryMock;
    private readonly GetOutboxMessagesRequestDtoValidator _validator;
    private readonly GetFailedOutboxMessagesUseCase _useCase;

    public GetFailedOutboxMessagesUseCaseTests()
    {
        _repositoryMock = new Mock<IAdministrationOutboxRepository>();
        _validator = new GetOutboxMessagesRequestDtoValidator();
        _useCase = new GetFailedOutboxMessagesUseCase(_validator, _repositoryMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WithoutStatus_ShouldDefaultToDeadStatus()
    {
        // Arrange
        var request = new GetOutboxMessagesRequestDto { Page = 1, PageSize = 20 };
        var expectedPaged = new Paged<OutboxMessageDto>
        {
            Items = new List<OutboxMessageDto>
            {
                new()
                {
                    Id = 7781,
                    EventUuid = Guid.Parse("2b6f1c9e-3d4a-4f7b-8e21-9c0d5a6b7e88"),
                    EventType = "DISH_UPSERTED",
                    AggregateKey = "dish:105",
                    Payload = new { dish_id = 105, name = "Bún bò Huế" },
                    Status = "dead",
                    AttemptCount = 8,
                    LastError = "503 VECTOR_NOT_READY",
                    CreatedAt = new DateTime(2026, 10, 2, 2, 0, 0, DateTimeKind.Utc),
                    NextAttemptAt = null
                }
            },
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        _repositoryMock
            .Setup(repo => repo.GetOutboxMessagesAsync("dead", null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPaged);

        // Act
        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.TotalItems.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].Id.Should().Be(7781);
        result.Items[0].Status.Should().Be("dead");
        result.Items[0].EventType.Should().Be("DISH_UPSERTED");
        result.Items[0].LastError.Should().Be("503 VECTOR_NOT_READY");
        _repositoryMock.Verify(repo => repo.GetOutboxMessagesAsync("dead", null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WithFailedStatus_ShouldNormalizeToDead()
    {
        // Arrange
        var request = new GetOutboxMessagesRequestDto { Status = "failed", Page = 1, PageSize = 10 };
        var expectedPaged = new Paged<OutboxMessageDto> { Page = 1, PageSize = 10, TotalItems = 0, TotalPages = 0 };

        _repositoryMock
            .Setup(repo => repo.GetOutboxMessagesAsync("dead", null, 1, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPaged);

        // Act
        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        _repositoryMock.Verify(repo => repo.GetOutboxMessagesAsync("dead", null, 1, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WithEventTypeFilter_ShouldPassNormalizedEventType()
    {
        // Arrange
        var request = new GetOutboxMessagesRequestDto { Status = "dead", EventType = "DISH_UPSERTED", Page = 2, PageSize = 15 };
        var expectedPaged = new Paged<OutboxMessageDto> { Page = 2, PageSize = 15, TotalItems = 16, TotalPages = 2 };

        _repositoryMock
            .Setup(repo => repo.GetOutboxMessagesAsync("dead", "DISH_UPSERTED", 2, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPaged);

        // Act
        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Page.Should().Be(2);
        result.TotalPages.Should().Be(2);
        _repositoryMock.Verify(repo => repo.GetOutboxMessagesAsync("dead", "DISH_UPSERTED", 2, 15, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_InvalidPage_ShouldThrowValidationException()
    {
        // Arrange: Page = 0 is invalid
        var request = new GetOutboxMessagesRequestDto { Page = 0, PageSize = 20 };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();

        _repositoryMock.Verify(repo => repo.GetOutboxMessagesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_InvalidStatus_ShouldThrowValidationException()
    {
        // Arrange: Unknown status
        var request = new GetOutboxMessagesRequestDto { Status = "invalid_status" };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();

        _repositoryMock.Verify(repo => repo.GetOutboxMessagesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
