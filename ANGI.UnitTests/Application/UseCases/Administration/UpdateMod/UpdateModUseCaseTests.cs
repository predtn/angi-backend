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

/// <summary>Verifies UpdateModUseCase changes a MOD's status, revokes sessions on suspension and stays idempotent for unchanged input.</summary>
public sealed class UpdateModUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);

    // TEST-01: Update both fields, revoke every active session, audit, save, commit, and invalidate status cache.
    /// <summary>Verifies the complete successful ADM-M4 deactivation transaction.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDeactivating_ShouldUpdateRevokeAuditCommitAndInvalidate()
    {
        // STEP-01: Get mod from ExistingMod and build the sessions collection.
        var mod = ExistingMod();
        var sessions = new[] { new UserSession(), new UserSession() };
        // STEP-02: Declare auditEntry to capture a call argument; mock IAdministrationModRepository and 2 more; stub repository.GetModForUpdateAsync and 2 more.
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModForUpdateAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        repository.Setup(item => item.GetActiveSessionsAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(sessions);
        var audit = new Mock<IAuditLogService>();
        AuditLogEntry? auditEntry = null;
        audit.Setup(item => item.Add(It.IsAny<AuditLogEntry>())).Callback<AuditLogEntry>(entry => auditEntry = entry);
        var cache = new Mock<IAccountStatusCache>();
        // STEP-03: Get transaction from Transaction and get unitOfWork from UnitOfWork.
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        // STEP-04: Build useCase under test through Create.
        var useCase = Create(repository.Object, cache.Object, audit.Object, unitOfWork.Object);

        // STEP-05: Run useCase.ExecuteAsync with UpdateModRequestDto { DisplayName = " New Name ", Status = "deactivated" } and keep result.
        var result = await useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { DisplayName = " New Name ", Status = "deactivated" },
            CancellationToken.None);

        // STEP-06: Assert result.DisplayName is "New Name" and result.Status is "deactivated".
        result.DisplayName.Should().Be("New Name");
        result.Status.Should().Be("deactivated");
        // STEP-07: Assert sessions only holds items matching the condition and auditEntry.Action is AuditActions.ModUpdated.
        sessions.Should().OnlyContain(session => session.RevokedAt == Now);
        auditEntry!.Action.Should().Be(AuditActions.ModUpdated);
        // STEP-08: Verify SaveChangesAsync is called once and CommitAsync is called once.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        // STEP-09: Verify Invalidate is called once.
        cache.Verify(item => item.Invalidate(42), Times.Once);
    }

    // TEST-02: Reactivate a MOD without reading sessions and clear stale suspension data.
    /// <summary>Verifies the active boundary changes status without attempting session restoration.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenReactivating_ShouldNotLoadSessions()
    {
        // STEP-01: Get mod from ExistingMod, set mod.Status to UserStatus.Deactivated and set mod.SuspendedUntil to Now.AddDays().
        var mod = ExistingMod();
        mod.Status = UserStatus.Deactivated;
        mod.SuspendedUntil = Now.AddDays(1);
        // STEP-02: Mock IAdministrationModRepository; stub repository.GetModForUpdateAsync.
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModForUpdateAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        // STEP-03: Build useCase under test through Create.
        var useCase = Create(repository.Object);

        // STEP-04: Run useCase.ExecuteAsync with UpdateModRequestDto { Status = "active" } and keep result.
        var result = await useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { Status = "active" },
            CancellationToken.None);

        // STEP-05: Assert result.Status is "active" and result.SuspendedUntil is null.
        result.Status.Should().Be("active");
        result.SuspendedUntil.Should().BeNull();
        // STEP-06: Verify GetActiveSessionsAsync is never called.
        repository.Verify(item => item.GetActiveSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Treat identical supplied values with no sessions as an idempotent no-op.
    /// <summary>Verifies no audit, save, commit, or cache invalidation occurs without a state change.</summary>
    [Fact]
    public async Task ExecuteAsync_WithIdenticalValues_ShouldReturnWithoutWriting()
    {
        // STEP-01: Mock IAdministrationModRepository, IAuditLogService and 1 more; stub repository.GetModForUpdateAsync and repository.GetActiveSessionsAsync.
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModForUpdateAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(ExistingMod());
        repository.Setup(item => item.GetActiveSessionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserSession>());
        var audit = new Mock<IAuditLogService>();
        var cache = new Mock<IAccountStatusCache>();
        // STEP-02: Get transaction from Transaction and get unitOfWork from UnitOfWork.
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        // STEP-03: Build useCase under test through Create.
        var useCase = Create(repository.Object, cache.Object, audit.Object, unitOfWork.Object);

        // STEP-04: Run useCase.ExecuteAsync with UpdateModRequestDto { DisplayName = "Old Name", Status = "active" }.
        await useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { DisplayName = "Old Name", Status = "active" },
            CancellationToken.None);

        // STEP-05: Verify audit receives no other calls, SaveChangesAsync is never called, CommitAsync is never called and cache receives no other calls.
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
        // STEP-01: Mock IAdministrationModRepository; stub repository.GetModForUpdateAsync.
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModForUpdateAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        // STEP-02: Build useCase under test through Create.
        var useCase = Create(repository.Object);

        // STEP-03: Wrap useCase.ExecuteAsync with UpdateModRequestDto { Status = "deactivated" } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { Status = "deactivated" },
            CancellationToken.None);

        // STEP-04: Assert act throws NotFoundException and exception.ErrorCode is "MOD_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("MOD_NOT_FOUND");
    }

    // TEST-05: Reject an unauthenticated invocation before opening a transaction.
    /// <summary>Verifies defense in depth for the ADM-M4 audit actor.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        // STEP-01: Mock IUnitOfWork.
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-02: Build useCase under test through Create.
        var useCase = Create(
            Mock.Of<IAdministrationModRepository>(),
            unitOfWork: unitOfWork.Object,
            currentUser: Mock.Of<ICurrentUserService>());

        // STEP-03: Wrap useCase.ExecuteAsync with UpdateModRequestDto { Status = "active" } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            42,
            new UpdateModRequestDto { Status = "active" },
            CancellationToken.None);

        // STEP-04: Assert act throws UnauthorizedException and exception.ErrorCode is "UNAUTHORIZED".
        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        // STEP-05: Verify unitOfWork receives no other calls.
        unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-06: Reject an empty patch before authentication or persistence work.
    /// <summary>Verifies validation is the first ADM-M4 operation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyPatch_ShouldStopBeforeDependencies()
    {
        // STEP-01: Mock IAdministrationModRepository and IUnitOfWork.
        var repository = new Mock<IAdministrationModRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-02: Build useCase under test through Create.
        var useCase = Create(repository.Object, unitOfWork: unitOfWork.Object);

        // STEP-03: Wrap useCase.ExecuteAsync with an empty UpdateModRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(42, new UpdateModRequestDto(), CancellationToken.None);

        // STEP-04: Assert act throws FluentValidation.ValidationException.
        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        // STEP-05: Verify repository receives no other calls and unitOfWork receives no other calls.
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

    /// <summary>Clock fixed at one instant so session revocation timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        /// <summary>Returns the deterministic session-revocation timestamp.</summary>
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
