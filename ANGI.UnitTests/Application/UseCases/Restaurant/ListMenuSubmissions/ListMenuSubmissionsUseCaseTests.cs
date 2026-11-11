using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.ListMenuSubmissions;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.ListMenuSubmissions;

/// <summary>Verifies ListMenuSubmissionsUseCase filters and pages the owner's menu submissions.</summary>
public sealed class ListMenuSubmissionsUseCaseTests
{
    // TEST-01: Apply status and pagination while calculating total pages.
    /// <summary>Verifies OWN-12 filter parsing and page metadata.</summary>
    [Fact]
    public async Task ExecuteAsync_WithFilter_ShouldReturnPagedHistory()
    {
        // STEP-01: Mock IRestaurantMenuRepository and ICurrentUserService; stub repository.GetSubmissionsAsync and currentUser.UserId.
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
        // STEP-02: Create the ListMenuSubmissionsUseCase under test as useCase.
        var useCase = new ListMenuSubmissionsUseCase(
            new ListMenuSubmissionsRequestDtoValidator(),
            repository.Object,
            currentUser.Object);

        // STEP-03: Run useCase.ExecuteAsync with ListMenuSubmissionsRequestDto { Status = "pending", Page = 2, PageSize = 10 } and keep result.
        var result = await useCase.ExecuteAsync(
            new ListMenuSubmissionsRequestDto { Status = "pending", Page = 2, PageSize = 10 },
            CancellationToken.None);

        // STEP-04: Assert result.Items has exactly one matching item, result.Items.Status is "pending", result.TotalItems is 21 and result.TotalPages is 3.
        result.Items.Should().ContainSingle().Which.Status.Should().Be("pending");
        result.TotalItems.Should().Be(21);
        result.TotalPages.Should().Be(3);
    }

    // TEST-02: Apply the documented default filter and pagination values.
    /// <summary>Ensures the documented defaults apply when no filter is sent.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutFilter_ShouldUseDefaultPagination()
    {
        // STEP-01: Mock IRestaurantMenuRepository; stub repository.GetSubmissionsAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionsAsync(
                12, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<MenuSubmission>(), 0L));

        // STEP-02: Run CreateUseCase().ExecuteAsync with an empty ListMenuSubmissionsRequestDto and keep result.
        var result = await CreateUseCase(repository).ExecuteAsync(
            new ListMenuSubmissionsRequestDto(),
            CancellationToken.None);

        // STEP-03: Assert result.Page is 1, result.PageSize is 20, result.Items is empty and result.TotalPages is 0.
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.Items.Should().BeEmpty();
        result.TotalPages.Should().Be(0);
    }

    // TEST-03: Parse every supported status regardless of casing.
    /// <summary>Ensures status filters work regardless of letter case.</summary>
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
        // STEP-01: Mock IRestaurantMenuRepository; stub repository.GetSubmissionsAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionsAsync(
                12, expected, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<MenuSubmission>(), 0L));

        // STEP-02: Run CreateUseCase().ExecuteAsync with ListMenuSubmissionsRequestDto { Status = value }.
        await CreateUseCase(repository).ExecuteAsync(
            new ListMenuSubmissionsRequestDto { Status = value },
            CancellationToken.None);

        // STEP-03: Verify every setup on repository ran.
        repository.VerifyAll();
    }

    // TEST-04: Return an empty page while preserving totals when page exceeds the result range.
    /// <summary>Ensures a page past the end is empty but still reports the real totals.</summary>
    [Fact]
    public async Task ExecuteAsync_WithPageBeyondTotal_ShouldReturnEmptyPageAndStableMetadata()
    {
        // STEP-01: Mock IRestaurantMenuRepository; stub repository.GetSubmissionsAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionsAsync(
                12, null, 9, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<MenuSubmission>(), 21L));

        // STEP-02: Run CreateUseCase().ExecuteAsync with ListMenuSubmissionsRequestDto { Page = 9 } and keep result.
        var result = await CreateUseCase(repository).ExecuteAsync(
            new ListMenuSubmissionsRequestDto { Page = 9 },
            CancellationToken.None);

        // STEP-03: Assert result.Items is empty, result.TotalItems is 21 and result.TotalPages is 2.
        result.Items.Should().BeEmpty();
        result.TotalItems.Should().Be(21);
        result.TotalPages.Should().Be(2);
    }

    /// <summary>Creates the use case for owner 12 with the real validator over the given repository.</summary>
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
