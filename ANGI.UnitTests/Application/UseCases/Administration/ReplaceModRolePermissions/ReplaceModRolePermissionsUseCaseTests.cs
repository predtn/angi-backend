using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.ReplaceModRolePermissions;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.ReplaceModRolePermissions;

public sealed class ReplaceModRolePermissionsUseCaseTests
{
    // TEST-01: Replace defaults in catalog order, audit them, save once, and commit.
    /// <summary>Verifies the complete successful ADM-P4 transaction.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidCodes_ShouldPersistAuditAndReturnSortedDefaults()
    {
        var repository = RepositoryWithCatalog();
        var audit = new Mock<IAuditLogService>();
        AuditLogEntry? auditEntry = null;
        audit.Setup(item => item.Add(It.IsAny<AuditLogEntry>()))
            .Callback<AuditLogEntry>(entry => auditEntry = entry);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(7);
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        var useCase = Create(repository.Object, currentUser.Object, audit.Object, unitOfWork.Object);

        var result = await useCase.ExecuteAsync(new ReplaceModRolePermissionsRequestDto
        {
            PermissionCodes = ["DASHBOARD_VIEW", "USER_SUSPEND"]
        }, CancellationToken.None);

        result.Role.Should().Be("MOD");
        result.PermissionCodes.Should().Equal("USER_SUSPEND", "DASHBOARD_VIEW");
        repository.Verify(item => item.ReplaceModRoleDefaultsAsync(
            3,
            It.Is<IReadOnlyList<short>>(ids => ids.SequenceEqual(new short[] { 1, 13 })),
            It.IsAny<CancellationToken>()), Times.Once);
        auditEntry.Should().NotBeNull();
        auditEntry!.Action.Should().Be(AuditActions.RolePermissionsChanged);
        auditEntry.EntityType.Should().Be(AuditEntityTypes.Role);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Accept an empty replacement and remove every MOD role default.
    /// <summary>Verifies the lower collection boundary for ADM-P4.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyCodes_ShouldPersistEmptyDefaults()
    {
        var repository = RepositoryWithCatalog();
        var useCase = CreateAuthenticated(repository.Object);

        var result = await useCase.ExecuteAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = [] },
            CancellationToken.None);

        result.PermissionCodes.Should().BeEmpty();
        repository.Verify(item => item.ReplaceModRoleDefaultsAsync(
            3, It.Is<IReadOnlyList<short>>(ids => ids.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Return UNKNOWN_PERMISSION for a code outside the permission catalog.
    /// <summary>Verifies the documented unknown-code boundary before persistence.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownCode_ShouldThrowDocumentedError()
    {
        var useCase = CreateAuthenticated(RepositoryWithCatalog().Object);

        var act = () => useCase.ExecuteAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = ["VERIFICATION_APPROVE"] },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("UNKNOWN_PERMISSION");
    }

    // TEST-04: Reject every permission reserved exclusively for administrators.
    /// <summary>Verifies both documented admin-only boundaries use PERMISSION_NOT_GRANTABLE.</summary>
    [Theory]
    [InlineData("MOD_MANAGE")]
    [InlineData("SYSTEM_AUDIT_VIEW")]
    public async Task ExecuteAsync_WithAdminOnlyCode_ShouldThrowDocumentedError(string code)
    {
        var useCase = CreateAuthenticated(RepositoryWithCatalog().Object);

        var act = () => useCase.ExecuteAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = [code] },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("PERMISSION_NOT_GRANTABLE");
    }

    // TEST-05: Reject unauthenticated invocation before starting a transaction.
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
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = [] },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        unitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Validate the request before authentication, reads, or transaction work.
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
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = null! },
            CancellationToken.None);

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        repository.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
    }

    /// <summary>Creates a repository mock with the seeded catalog and representative current defaults.</summary>
    private static Mock<IAdministrationPermissionRepository> RepositoryWithCatalog()
    {
        var repository = new Mock<IAdministrationPermissionRepository>();
        repository.Setup(item => item.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PermissionCatalogSnapshot(
                3,
                [
                    new(1, "USER_SUSPEND", "Suspend"),
                    new(11, "MOD_MANAGE", "Manage Mods"),
                    new(12, "SYSTEM_AUDIT_VIEW", "Audit"),
                    new(13, "DASHBOARD_VIEW", "Dashboard")
                ],
                ["USER_SUSPEND"]));
        return repository;
    }

    /// <summary>Creates a use case with an authenticated actor and successful transaction dependencies.</summary>
    private static ReplaceModRolePermissionsUseCase CreateAuthenticated(IAdministrationPermissionRepository repository)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(7);
        return Create(repository, currentUser.Object, Mock.Of<IAuditLogService>(), UnitOfWork(Transaction()).Object);
    }

    /// <summary>Creates the system under test with the real request validator.</summary>
    private static ReplaceModRolePermissionsUseCase Create(
        IAdministrationPermissionRepository repository,
        ICurrentUserService currentUser,
        IAuditLogService audit,
        IUnitOfWork unitOfWork) => new(
            new ReplaceModRolePermissionsRequestDtoValidator(),
            repository,
            currentUser,
            audit,
            unitOfWork);

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
}
