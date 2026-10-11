using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.ListMenuSubmissions;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.ListMenuSubmissions;

public sealed class ListMenuSubmissionsUseCaseTests
{
    // TEST-01: Apply status and pagination while calculating total pages.
    /// <summary>Verifies OWN-12 filter parsing and page metadata.</summary>
    [Fact]
    public async Task ExecuteAsync_WithFilter_ShouldReturnPagedHistory()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionsAsync(
                12,
                MenuSubmissionStatus.Pending,
                2,
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new MenuSubmission { Id = 14, Status = MenuSubmissionStatus.Pending }], 21L));
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        var useCase = new ListMenuSubmissionsUseCase(
            new ListMenuSubmissionsRequestDtoValidator(),
            repository.Object,
            currentUser.Object);

        var result = await useCase.ExecuteAsync(
            new ListMenuSubmissionsRequestDto { Status = "pending", Page = 2, PageSize = 10 },
            CancellationToken.None);

        result.Items.Should().ContainSingle().Which.Status.Should().Be("pending");
        result.TotalItems.Should().Be(21);
        result.TotalPages.Should().Be(3);
    }

    // TEST-02: Apply the documented default filter and pagination values.
    [Fact]
    public async Task ExecuteAsync_WithoutFilter_ShouldUseDefaultPagination()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionsAsync(
                12, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<MenuSubmission>(), 0L));

        var result = await CreateUseCase(repository).ExecuteAsync(
            new ListMenuSubmissionsRequestDto(),
            CancellationToken.None);

        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.Items.Should().BeEmpty();
        result.TotalPages.Should().Be(0);
    }

    // TEST-03: Parse every supported status regardless of casing.
    [Theory]
    [InlineData("DRAFT", MenuSubmissionStatus.Draft)]
    [InlineData("Pending", MenuSubmissionStatus.Pending)]
    [InlineData("APPROVED", MenuSubmissionStatus.Approved)]
    [InlineData("Rejected", MenuSubmissionStatus.Rejected)]
    [InlineData("CANCELLED", MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithStatusCasing_ShouldParseStatus(
        string value,
        MenuSubmissionStatus expected)
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionsAsync(
                12, expected, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<MenuSubmission>(), 0L));

        await CreateUseCase(repository).ExecuteAsync(
            new ListMenuSubmissionsRequestDto { Status = value },
            CancellationToken.None);

        repository.VerifyAll();
    }

    // TEST-04: Return an empty page while preserving totals when page exceeds the result range.
    [Fact]
    public async Task ExecuteAsync_WithPageBeyondTotal_ShouldReturnEmptyPageAndStableMetadata()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionsAsync(
                12, null, 9, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<MenuSubmission>(), 21L));

        var result = await CreateUseCase(repository).ExecuteAsync(
            new ListMenuSubmissionsRequestDto { Page = 9 },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.TotalItems.Should().Be(21);
        result.TotalPages.Should().Be(2);
    }

    private static ListMenuSubmissionsUseCase CreateUseCase(
        Mock<IRestaurantMenuRepository> repository)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        return new ListMenuSubmissionsUseCase(
            new ListMenuSubmissionsRequestDtoValidator(),
            repository.Object,
            currentUser.Object);
    }
}
