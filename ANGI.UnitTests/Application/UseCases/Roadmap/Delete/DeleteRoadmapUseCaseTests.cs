using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Roadmap.Delete;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.Delete;

public sealed class DeleteRoadmapUseCaseTests
{
    // TEST-01: Soft-delete an owned roadmap at the injected UTC time and commit exactly once.
    /// <summary>Verifies the complete successful RM-05 transaction and persistence path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedRoadmap_ShouldSetDeletedAtAndCommit()
    {
        var now = new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.Zero);
        var roadmap = new Domain.Entities.Roadmap { Id = 88, UserId = 12, Title = "Trip", NumDays = 1 };
        var fixture = CreateFixture(roadmap, now, 12);
        using var cancellationSource = new CancellationTokenSource();

        await fixture.UseCase.ExecuteAsync(88, cancellationSource.Token);

        roadmap.DeletedAt.Should().Be(now.UtcDateTime);
        fixture.Repository.Verify(item => item.LockAndGetTrackedForDeleteAsync(
            88, 12, cancellationSource.Token), Times.Once);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(cancellationSource.Token), Times.Once);
        fixture.Transaction.Verify(item => item.CommitAsync(cancellationSource.Token), Times.Once);
    }

    // TEST-02: Reject an unauthenticated caller before opening a transaction or reading persistence.
    /// <summary>Verifies Application authorization remains enforced independently of the controller.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorizedWithoutPersistence()
    {
        var fixture = CreateFixture(new Domain.Entities.Roadmap(), DateTimeOffset.UtcNow, null);

        var act = () => fixture.UseCase.ExecuteAsync(88, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        fixture.Repository.Verify(item => item.LockAndGetTrackedForDeleteAsync(
            It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Hide a missing, already-deleted, or foreign roadmap behind ROADMAP_NOT_FOUND.
    /// <summary>Verifies the ownership-filtered absence path rolls back without saving or committing.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutOwnedVisibleRoadmap_ShouldThrowNotFoundWithoutSaving()
    {
        var fixture = CreateFixture(null, DateTimeOffset.UtcNow, 12);

        var act = () => fixture.UseCase.ExecuteAsync(404, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        fixture.Repository.Verify(item => item.LockAndGetTrackedForDeleteAsync(
            404, 12, It.IsAny<CancellationToken>()), Times.Once);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Creates RM-05 with deterministic identity, time, transaction, and repository behavior.</summary>
    private static Fixture CreateFixture(
        Domain.Entities.Roadmap? roadmap,
        DateTimeOffset now,
        int? userId)
    {
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(item => item.LockAndGetTrackedForDeleteAsync(
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(roadmap);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(userId);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        var timeProvider = new Mock<TimeProvider>();
        timeProvider.Setup(item => item.GetUtcNow()).Returns(now);
        return new Fixture(
            new DeleteRoadmapUseCase(
                repository.Object,
                currentUser.Object,
                unitOfWork.Object,
                timeProvider.Object),
            repository,
            unitOfWork,
            transaction);
    }

    private sealed record Fixture(
        DeleteRoadmapUseCase UseCase,
        Mock<IRoadmapRepository> Repository,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IUnitOfWorkTransaction> Transaction);
}
