using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.UpdateMod;
using ANGI.Application.UseCases.Validators.Administration;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.UpdateMod;

public sealed class UpdateModUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);

    // TEST-01: Update both fields, revoke every active session, audit, save, commit, and invalidate status cache.
    /// <summary>Verifies the complete successful ADM-M4 deactivation transaction.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDeactivating_ShouldUpdateRevokeAuditCommitAndInvalidate()
    {
        var mod = ExistingMod();
        var sessions = new[] { new UserSession(), new UserSession() };
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModForUpdateAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        repository.Setup(item => item.GetActiveSessionsAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(sessions);
        var audit = new Mock<IAuditLogService>();
        AuditLogEntry? auditEntry = null;
        audit.Setup(item => item.Add(It.IsAny<AuditLogEntry>())).Callback<AuditLogEntry>(entry => auditEntry = entry);
        var cache = new Mock<IAccountStatusCache>();
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        var useCase = Create(repository.Object, cache.Object, audit.Object, unitOfWork.Object);

        var result = await useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { DisplayName = " New Name ", Status = "deactivated" },
            CancellationToken.None);

        result.DisplayName.Should().Be("New Name");
        result.Status.Should().Be("deactivated");
        sessions.Should().OnlyContain(session => session.RevokedAt == Now);
        auditEntry!.Action.Should().Be(AuditActions.ModUpdated);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(item => item.Invalidate(42), Times.Once);
    }

    // TEST-02: Reactivate a MOD without reading sessions and clear stale suspension data.
    /// <summary>Verifies the active boundary changes status without attempting session restoration.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenReactivating_ShouldNotLoadSessions()
    {
        var mod = ExistingMod();
        mod.Status = UserStatus.Deactivated;
        mod.SuspendedUntil = Now.AddDays(1);
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModForUpdateAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        var useCase = Create(repository.Object);

        var result = await useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { Status = "active" },
            CancellationToken.None);

        result.Status.Should().Be("active");
        result.SuspendedUntil.Should().BeNull();
        repository.Verify(item => item.GetActiveSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Treat identical supplied values with no sessions as an idempotent no-op.
    /// <summary>Verifies no audit, save, commit, or cache invalidation occurs without a state change.</summary>
    [Fact]
    public async Task ExecuteAsync_WithIdenticalValues_ShouldReturnWithoutWriting()
    {
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModForUpdateAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(ExistingMod());
        repository.Setup(item => item.GetActiveSessionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserSession>());
        var audit = new Mock<IAuditLogService>();
        var cache = new Mock<IAccountStatusCache>();
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        var useCase = Create(repository.Object, cache.Object, audit.Object, unitOfWork.Object);

        await useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { DisplayName = "Old Name", Status = "active" },
            CancellationToken.None);

        audit.VerifyNoOtherCalls();
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        cache.VerifyNoOtherCalls();
    }

    // TEST-04: Return MOD_NOT_FOUND for a missing, deleted, or non-MOD target.
    /// <summary>Verifies ADM-M4 uses its documented resource error before mutations.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutMod_ShouldThrowDocumentedError()
    {
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModForUpdateAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        var useCase = Create(repository.Object);

        var act = () => useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { Status = "deactivated" },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("MOD_NOT_FOUND");
    }

    // TEST-05: Reject an unauthenticated invocation before opening a transaction.
    /// <summary>Verifies defense in depth for the ADM-M4 audit actor.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = Create(
            Mock.Of<IAdministrationModRepository>(),
            unitOfWork: unitOfWork.Object,
            currentUser: Mock.Of<ICurrentUserService>());

        var act = () => useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { Status = "active" },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-06: Reject an empty patch before authentication or persistence work.
    /// <summary>Verifies validation is the first ADM-M4 operation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyPatch_ShouldStopBeforeDependencies()
    {
        var repository = new Mock<IAdministrationModRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = Create(repository.Object, unitOfWork: unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(42, new UpdateModRequestDto(), CancellationToken.None);

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        repository.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
    }

    /// <summary>Creates a representative tracked active MOD entity.</summary>
    private static User ExistingMod() => new()
    {
        Id = 42,
        Email = "mod@angi.vn",
        DisplayName = "Old Name",
        Status = UserStatus.Active,
        Role = new Role { Id = 3, Code = "MOD", Name = "Mod" }
    };

    /// <summary>Creates the system under test with deterministic defaults and the real validator.</summary>
    private static UpdateModUseCase Create(
        IAdministrationModRepository repository,
        IAccountStatusCache? cache = null,
        IAuditLogService? audit = null,
        IUnitOfWork? unitOfWork = null,
        ICurrentUserService? currentUser = null)
    {
        currentUser ??= CurrentUser();
        return new UpdateModUseCase(
            new UpdateModRequestDtoValidator(),
            repository,
            currentUser,
            cache ?? Mock.Of<IAccountStatusCache>(),
            audit ?? Mock.Of<IAuditLogService>(),
            unitOfWork ?? UnitOfWork(Transaction()).Object,
            new FixedTimeProvider(Now));
    }

    /// <summary>Creates an authenticated ADMIN identity for audit attribution.</summary>
    private static ICurrentUserService CurrentUser()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(7);
        return currentUser.Object;
    }

    /// <summary>Creates a transaction mock with a successful commit.</summary>
    private static Mock<IUnitOfWorkTransaction> Transaction()
    {
        var transaction = new Mock<IUnitOfWorkTransaction>();
        transaction.Setup(item => item.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        transaction.Setup(item => item.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return transaction;
    }

    /// <summary>Creates a Unit of Work mock returning the supplied transaction.</summary>
    private static Mock<IUnitOfWork> UnitOfWork(Mock<IUnitOfWorkTransaction> transaction)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        return unitOfWork;
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        /// <summary>Returns the deterministic session-revocation timestamp.</summary>
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
