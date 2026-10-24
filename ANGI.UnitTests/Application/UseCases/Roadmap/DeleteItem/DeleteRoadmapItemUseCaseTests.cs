using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Roadmap.DeleteItem;
using ANGI.Domain.Entities;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.DeleteItem;

public sealed class DeleteRoadmapItemUseCaseTests
{
    // TEST-01: Delete the matching owned roadmap item and commit exactly once.
    /// <summary>Verifies the complete successful RM-09 transaction and persistence path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedMatchingItem_ShouldRemoveAndCommit()
    {
        var item = new RoadmapItem { Id = 1502, RoadmapDayId = 301, DishId = 105 };
        var fixture = CreateFixture(item, 12);
        using var cancellationSource = new CancellationTokenSource();

        await fixture.UseCase.ExecuteAsync(88, 1502, cancellationSource.Token);

        fixture.Repository.Verify(repository => repository.LockAndGetOwnedItemForDeleteAsync(
            88, 1502, 12, cancellationSource.Token), Times.Once);
        fixture.Repository.Verify(repository => repository.RemoveItem(item), Times.Once);
        fixture.UnitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(cancellationSource.Token), Times.Once);
        fixture.Transaction.Verify(transaction => transaction.CommitAsync(cancellationSource.Token), Times.Once);
    }

    // TEST-02: Reject an unauthenticated caller before opening a transaction or accessing persistence.
    /// <summary>Verifies RM-09 authorization is enforced within Application as a defense in depth.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorizedWithoutPersistence()
    {
        var fixture = CreateFixture(new RoadmapItem(), null);

        var act = () => fixture.UseCase.ExecuteAsync(88, 1502, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        fixture.Repository.Verify(repository => repository.LockAndGetOwnedItemForDeleteAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Repository.Verify(repository => repository.RemoveItem(It.IsAny<RoadmapItem>()), Times.Never);
        fixture.UnitOfWork.Verify(unitOfWork => unitOfWork.BeginTransactionAsync(
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Conceal missing, mismatched, foreign, deleted-parent, zero, and negative targets behind one item error.
    /// <summary>Verifies every ownership and identifier miss returns ROADMAP_ITEM_NOT_FOUND without mutation.</summary>
    [Theory]
    [InlineData(88, 404)]
    [InlineData(99, 1502)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public async Task ExecuteAsync_WithoutOwnedMatchingItem_ShouldThrowNotFoundWithoutSaving(
        long roadmapId,
        long itemId)
    {
        var fixture = CreateFixture(null, 12);

        var act = () => fixture.UseCase.ExecuteAsync(roadmapId, itemId, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_ITEM_NOT_FOUND");
        fixture.Repository.Verify(repository => repository.LockAndGetOwnedItemForDeleteAsync(
            roadmapId, itemId, 12, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Repository.Verify(repository => repository.RemoveItem(It.IsAny<RoadmapItem>()), Times.Never);
        fixture.UnitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(transaction => transaction.CommitAsync(
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Creates RM-09 with deterministic identity, transaction, and repository behavior.</summary>
    private static Fixture CreateFixture(RoadmapItem? item, int? userId)
    {
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(value => value.LockAndGetOwnedItemForDeleteAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(userId);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);

        return new Fixture(
            new DeleteRoadmapItemUseCase(repository.Object, currentUser.Object, unitOfWork.Object),
            repository,
            unitOfWork,
            transaction);
    }

    private sealed record Fixture(
        DeleteRoadmapItemUseCase UseCase,
        Mock<IRoadmapRepository> Repository,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IUnitOfWorkTransaction> Transaction);
}
