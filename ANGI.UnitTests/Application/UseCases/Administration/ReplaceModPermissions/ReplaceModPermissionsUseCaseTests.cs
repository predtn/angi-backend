using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.ReplaceModPermissions;
using ANGI.Application.UseCases.Validators.Administration;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.ReplaceModPermissions;

public sealed class ReplaceModPermissionsUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);

    // TEST-01: Replace the complete override set, audit it, save once, and commit.
    /// <summary>Verifies the full successful ADM-P3 transaction including effective permission calculation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidReplacement_ShouldPersistAuditAndReturnEffectivePermissions()
    {
        var repository = RepositoryWithState();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(7);
        var audit = new Mock<IAuditLogService>();
        AuditLogEntry? auditEntry = null;
        audit.Setup(item => item.Add(It.IsAny<AuditLogEntry>()))
            .Callback<AuditLogEntry>(entry => auditEntry = entry);
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        var useCase = Create(repository.Object, currentUser.Object, audit.Object, unitOfWork.Object);
        var request = new ReplaceModPermissionsRequestDto
        {
            Overrides =
            [
                new PermissionOverrideDto { PermissionCode = "DASHBOARD_VIEW", Effect = "allow" },
                new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "deny" }
            ]
        };

        var result = await useCase.ExecuteAsync(42, request, CancellationToken.None);

        result.Overrides.Select(item => item.PermissionCode).Should().Equal("USER_BAN", "DASHBOARD_VIEW");
        result.Effective.Should().Equal("USER_SUSPEND", "DASHBOARD_VIEW");
        repository.Verify(item => item.ReplaceModOverridesAsync(
            42,
            7,
            Now,
            It.Is<IReadOnlyList<PermissionOverrideChange>>(changes =>
                changes.Count == 2 && changes[0].PermissionId == 2 && changes[1].PermissionId == 3),
            It.IsAny<CancellationToken>()), Times.Once);
        auditEntry.Should().NotBeNull();
        auditEntry!.Action.Should().Be(AuditActions.ModPermissionsChanged);
        auditEntry.EntityType.Should().Be(AuditEntityTypes.User);
        auditEntry.SubjectUserId.Should().Be(42);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Accept an empty replacement and fall back to role defaults only.
    /// <summary>Verifies the documented empty-array boundary removes every user override.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyReplacement_ShouldReturnRoleDefaultsOnly()
    {
        var repository = RepositoryWithState();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(7);
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        var useCase = Create(repository.Object, currentUser.Object, Mock.Of<IAuditLogService>(), unitOfWork.Object);

        var result = await useCase.ExecuteAsync(
            42,
            new ReplaceModPermissionsRequestDto { Overrides = [] },
            CancellationToken.None);

        result.Overrides.Should().BeEmpty();
        result.Effective.Should().Equal("USER_SUSPEND", "USER_BAN");
        repository.Verify(item => item.ReplaceModOverridesAsync(
            42, 7, Now, It.Is<IReadOnlyList<PermissionOverrideChange>>(changes => changes.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Return UNKNOWN_PERMISSION before any replacement is tracked.
    /// <summary>Verifies an unrecognized catalog code uses the documented ADM-P3 error.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownPermission_ShouldThrowDocumentedError()
    {
        var repository = RepositoryWithState();
        var useCase = CreateAuthenticated(repository.Object);
        var request = new ReplaceModPermissionsRequestDto
        {
            Overrides = [new PermissionOverrideDto { PermissionCode = "UNKNOWN", Effect = "allow" }]
        };

        var act = () => useCase.ExecuteAsync(42, request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("UNKNOWN_PERMISSION");
        repository.Verify(item => item.ReplaceModOverridesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<IReadOnlyList<PermissionOverrideChange>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reject every permission that the API reserves for administrators.
    /// <summary>Verifies both documented admin-only boundaries use PERMISSION_NOT_GRANTABLE.</summary>
    [Theory]
    [InlineData("MOD_MANAGE")]
    [InlineData("SYSTEM_AUDIT_VIEW")]
    public async Task ExecuteAsync_WithAdminOnlyPermission_ShouldThrowDocumentedError(string code)
    {
        var repository = RepositoryWithState();
        var useCase = CreateAuthenticated(repository.Object);
        var request = new ReplaceModPermissionsRequestDto
        {
            Overrides = [new PermissionOverrideDto { PermissionCode = code, Effect = "allow" }]
        };

        var act = () => useCase.ExecuteAsync(42, request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("PERMISSION_NOT_GRANTABLE");
    }

    // TEST-05: Return MOD_NOT_FOUND before catalog lookup and persistence.
    /// <summary>Verifies missing, deleted, and non-MOD users share the documented resource error.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutMod_ShouldThrowModNotFound()
    {
        var repository = new Mock<IAdministrationPermissionRepository>();
        repository.Setup(item => item.GetModPermissionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModPermissionsSnapshot?)null);
        var useCase = CreateAuthenticated(repository.Object);

        var act = () => useCase.ExecuteAsync(
            42,
            new ReplaceModPermissionsRequestDto { Overrides = [] },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("MOD_NOT_FOUND");
        repository.Verify(item => item.GetCatalogAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Reject an unauthenticated invocation before starting a transaction.
    /// <summary>Verifies defense in depth for the audit actor and transaction boundary.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = Create(
            Mock.Of<IAdministrationPermissionRepository>(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IAuditLogService>(),
            unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(
            42,
            new ReplaceModPermissionsRequestDto { Overrides = [] },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        unitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-07: Validate the request before authentication, reads, or transaction work.
    /// <summary>Verifies the coding-rule requirement that validation is the first operation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldStopBeforeDependencies()
    {
        var repository = new Mock<IAdministrationPermissionRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = Create(
            repository.Object,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IAuditLogService>(),
            unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(
            42,
            new ReplaceModPermissionsRequestDto { Overrides = null! },
            CancellationToken.None);

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        repository.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
    }

    /// <summary>Creates a repository mock with representative defaults, overrides, and catalog entries.</summary>
    private static Mock<IAdministrationPermissionRepository> RepositoryWithState()
    {
        var repository = new Mock<IAdministrationPermissionRepository>();
        repository.Setup(item => item.GetModPermissionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModPermissionsSnapshot(
                42,
                ["USER_SUSPEND", "USER_BAN"],
                [new(2, "USER_BAN", PermissionEffect.Allow)]));
        repository.Setup(item => item.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PermissionCatalogSnapshot(
                3,
                [
                    new(1, "USER_SUSPEND", "Suspend"),
                    new(2, "USER_BAN", "Ban"),
                    new(3, "DASHBOARD_VIEW", "Dashboard"),
                    new(11, "MOD_MANAGE", "Manage Mods"),
                    new(12, "SYSTEM_AUDIT_VIEW", "Audit")
                ],
                ["USER_SUSPEND", "USER_BAN"]));
        return repository;
    }

    /// <summary>Creates a use case with an authenticated actor and successful transaction dependencies.</summary>
    private static ReplaceModPermissionsUseCase CreateAuthenticated(IAdministrationPermissionRepository repository)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(7);
        return Create(
            repository,
            currentUser.Object,
            Mock.Of<IAuditLogService>(),
            UnitOfWork(Transaction()).Object);
    }

    /// <summary>Creates the system under test with the real request validator.</summary>
    private static ReplaceModPermissionsUseCase Create(
        IAdministrationPermissionRepository repository,
        ICurrentUserService currentUser,
        IAuditLogService audit,
        IUnitOfWork unitOfWork) => new(
            new ReplaceModPermissionsRequestDtoValidator(),
            repository,
            currentUser,
            audit,
            unitOfWork,
            new FixedTimeProvider(Now));

    /// <summary>Creates a transaction mock whose commit operation completes successfully.</summary>
    private static Mock<IUnitOfWorkTransaction> Transaction()
    {
        var transaction = new Mock<IUnitOfWorkTransaction>();
        transaction.Setup(item => item.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        transaction.Setup(item => item.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return transaction;
    }

    /// <summary>Creates a Unit of Work mock that returns the supplied transaction.</summary>
    private static Mock<IUnitOfWork> UnitOfWork(Mock<IUnitOfWorkTransaction> transaction)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        return unitOfWork;
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        /// <summary>Returns the deterministic UTC timestamp used for granted_at assertions.</summary>
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
