using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.GetMyRoadmaps;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.GetMyRoadmaps;

/// <summary>Verifies GetMyRoadmapsUseCase returns a validated page of the caller's own roadmaps.</summary>
public sealed class GetMyRoadmapsUseCaseTests
{
    // TEST-01: Return the requested page scoped to the authenticated traveler.
    /// <summary>Verifies RM-02 delegates valid pagination with the current user identifier.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidRequest_ShouldReturnOwnedRoadmapPage()
    {
        // STEP-01: Create expected as Paged<RoadmapSummaryDto>.
        var expected = new Paged<RoadmapSummaryDto>
        {
            Items = [new RoadmapSummaryDto { Id = 88, Title = "Trip" }],
            Page = 2,
            PageSize = 10,
            TotalItems = 11,
            TotalPages = 2
        };
        // STEP-02: Mock IRoadmapRepository; stub repository.GetByUserAsync.
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(item => item.GetByUserAsync(12, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, 12);

        // STEP-04: Run useCase.ExecuteAsync with GetMyRoadmapsRequestDto { Page = 2, PageSize = 10 } and keep result.
        var result = await useCase.ExecuteAsync(
            new GetMyRoadmapsRequestDto { Page = 2, PageSize = 10 },
            CancellationToken.None);

        // STEP-05: Assert result is the same instance as expected.
        result.Should().BeSameAs(expected);
        // STEP-06: Verify GetByUserAsync is called once.
        repository.Verify(item => item.GetByUserAsync(12, 2, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reject invalid page values before querying persistence.
    /// <summary>Verifies validation is the first RM-02 operation and protects the repository from invalid paging.</summary>
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task ExecuteAsync_WithInvalidPagination_ShouldThrowValidationException(int page, int pageSize)
    {
        // STEP-01: Mock IRoadmapRepository.
        var repository = new Mock<IRoadmapRepository>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, 12);

        // STEP-03: Wrap useCase.ExecuteAsync with GetMyRoadmapsRequestDto { Page = page, PageSize = pageSize } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            new GetMyRoadmapsRequestDto { Page = page, PageSize = pageSize },
            CancellationToken.None);

        // STEP-04: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
        // STEP-05: Verify GetByUserAsync is never called.
        repository.Verify(
            item => item.GetByUserAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // TEST-03: Reject a caller without an authenticated user identifier.
    /// <summary>Verifies Application ownership scoping does not rely solely on controller authorization.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        // STEP-01: Mock IRoadmapRepository.
        var repository = new Mock<IRoadmapRepository>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, null);

        // STEP-03: Wrap useCase.ExecuteAsync with an empty GetMyRoadmapsRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(new GetMyRoadmapsRequestDto(), CancellationToken.None);

        // STEP-04: Assert act throws UnauthorizedException and the thrown exception.ErrorCode is "UNAUTHORIZED".
        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        // STEP-05: Verify GetByUserAsync is never called.
        repository.Verify(
            item => item.GetByUserAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Creates RM-02 with real validation and a deterministic current identity.</summary>
    private static GetMyRoadmapsUseCase CreateUseCase(Mock<IRoadmapRepository> repository, int? userId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(userId);
        return new GetMyRoadmapsUseCase(
            new GetMyRoadmapsRequestDtoValidator(),
            repository.Object,
            currentUser.Object);
    }
}
