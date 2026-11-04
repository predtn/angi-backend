using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.GetDashboardTop;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetDashboardTop;

public sealed class GetDashboardTopUseCaseTests
{
    private readonly Mock<IAdministrationDashboardRepository> _repositoryMock;
    private readonly GetDashboardTopRequestDtoValidator _validator;
    private readonly GetDashboardTopUseCase _useCase;

    public GetDashboardTopUseCaseTests()
    {
        _repositoryMock = new Mock<IAdministrationDashboardRepository>();
        _validator = new GetDashboardTopRequestDtoValidator();
        _useCase = new GetDashboardTopUseCase(_validator, _repositoryMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldReturnTopItems()
    {
        // Arrange
        var request = new GetDashboardTopRequestDto { Type = "dishes", Limit = 5 };
        var expectedItems = new List<DashboardTopItemDto>
        {
            new() { Id = 105, Name = "Bún bò Huế", Score = 128, Subtitle = "Bún Bò Bà Diệu" }
        };

        _repositoryMock
            .Setup(repo => repo.GetTopAsync("dishes", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedItems);

        // Act
        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Type.Should().Be("dishes");
        result.Items.Should().HaveCount(1);
        result.Items[0].Id.Should().Be(105);
        result.Items[0].Score.Should().Be(128);
        result.Items[0].Subtitle.Should().Be("Bún Bò Bà Diệu");
        _repositoryMock.Verify(repo => repo.GetTopAsync("dishes", 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WithoutLimit_ShouldDefaultTo10()
    {
        // Arrange
        var request = new GetDashboardTopRequestDto { Type = "restaurants" };
        var expectedItems = new List<DashboardTopItemDto>();

        _repositoryMock
            .Setup(repo => repo.GetTopAsync("restaurants", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedItems);

        // Act
        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        _repositoryMock.Verify(repo => repo.GetTopAsync("restaurants", 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_InvalidType_ShouldThrowValidationException()
    {
        // Arrange
        var request = new GetDashboardTopRequestDto { Type = "invalid_category" };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();

        _repositoryMock.Verify(repo => repo.GetTopAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionPath_LimitOutOfRange_ShouldThrowValidationException()
    {
        // Arrange: limit > 50
        var request = new GetDashboardTopRequestDto { Type = "blogs", Limit = 51 };

        // Act & Assert
        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();

        _repositoryMock.Verify(repo => repo.GetTopAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
