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

public sealed class GetMyRoadmapsUseCaseTests
{
    // TEST-01: Return the requested page scoped to the authenticated traveler.
    /// <summary>Verifies RM-02 delegates valid pagination with the current user identifier.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidRequest_ShouldReturnOwnedRoadmapPage()
    {
        var expected = new Paged<RoadmapSummaryDto>
        {
            Items = [new RoadmapSummaryDto { Id = 88, Title = "Trip" }],
            Page = 2,
            PageSize = 10,
            TotalItems = 11,
            TotalPages = 2
        };
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(item => item.GetByUserAsync(12, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var useCase = CreateUseCase(repository, 12);

        var result = await useCase.ExecuteAsync(
            new GetMyRoadmapsRequestDto { Page = 2, PageSize = 10 },
            CancellationToken.None);

        result.Should().BeSameAs(expected);
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
        var repository = new Mock<IRoadmapRepository>();
        var useCase = CreateUseCase(repository, 12);

        var act = () => useCase.ExecuteAsync(
            new GetMyRoadmapsRequestDto { Page = page, PageSize = pageSize },
            CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        repository.Verify(
            item => item.GetByUserAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // TEST-03: Reject a caller without an authenticated user identifier.
    /// <summary>Verifies Application ownership scoping does not rely solely on controller authorization.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        var repository = new Mock<IRoadmapRepository>();
        var useCase = CreateUseCase(repository, null);

        var act = () => useCase.ExecuteAsync(new GetMyRoadmapsRequestDto(), CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
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
