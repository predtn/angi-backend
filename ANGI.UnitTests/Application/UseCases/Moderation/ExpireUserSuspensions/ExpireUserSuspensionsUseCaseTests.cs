using ANGI.Application.UseCases.Moderation.ExpireUserSuspensions;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.ExpireUserSuspensions;

/// <summary>Checks atomic account restoration and post-commit cache invalidation.</summary>
public sealed class ExpireUserSuspensionsUseCaseTests
{
    // TEST-01: Restore each expired account, clear its deadline and invalidate its cache after commit.
    /// <summary>Verifies a single save and commit for a bounded expiry batch.</summary>
    [Fact]
    public async Task RestoresExpiredBatch()
    {
        var world = new ModerationTestContext();
        var user = new User { Id = 12, Status = UserStatus.Suspended, SuspendedUntil = world.Clock.GetUtcNow().UtcDateTime };
        world.Repository.Setup(x => x.LockExpiredSuspensionsAsync(world.Clock.GetUtcNow().UtcDateTime, It.IsAny<CancellationToken>())).ReturnsAsync([user]);
        var committed = false;
        world.Transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).Callback(() => committed = true).Returns(Task.CompletedTask);
        world.Cache.Setup(x => x.Invalidate(12)).Callback(() => committed.Should().BeTrue());
        (await new ExpireUserSuspensionsUseCase(world.Repository.Object, world.UnitOfWork.Object, world.Cache.Object, world.Clock).ExecuteAsync(default)).Should().Be(1);
        user.Status.Should().Be(UserStatus.Active);
        user.SuspendedUntil.Should().BeNull();
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Cache.Verify(x => x.Invalidate(12), Times.Once);
    }

    // TEST-02: Empty expiry batches do not save or invalidate any account cache.
    /// <summary>Checks idle polling does not produce unnecessary writes.</summary>
    [Fact]
    public async Task EmptyBatchDoesNotSave()
    {
        var world = new ModerationTestContext();
        world.Repository.Setup(x => x.LockExpiredSuspensionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        (await new ExpireUserSuspensionsUseCase(world.Repository.Object, world.UnitOfWork.Object, world.Cache.Object, world.Clock).ExecuteAsync(default)).Should().Be(0);
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Cache.VerifyNoOtherCalls();
    }

    // TEST-03: Save or commit failure propagates and must not invalidate cached account status.
    /// <summary>Checks both transaction failure paths preserve cache consistency.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureDoesNotInvalidate(bool failCommit)
    {
        var world = new ModerationTestContext();
        world.Repository.Setup(x => x.LockExpiredSuspensionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([new User { Id = 12 }]);
        if (failCommit) world.Transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        else world.UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        var action = () => new ExpireUserSuspensionsUseCase(world.Repository.Object, world.UnitOfWork.Object, world.Cache.Object, world.Clock).ExecuteAsync(default);
        await action.Should().ThrowAsync<InvalidOperationException>();
        world.Cache.VerifyNoOtherCalls();
        world.Transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }
}
