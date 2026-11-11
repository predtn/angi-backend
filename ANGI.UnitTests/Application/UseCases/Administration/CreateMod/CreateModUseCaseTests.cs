using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.CreateMod;
using ANGI.Application.UseCases.Validators.Administration;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.CreateMod;

/// <summary>Verifies CreateModUseCase creates an invited MOD account with valid permission overrides and rejects conflicts.</summary>
public sealed class CreateModUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);

    // TEST-01: Create a verified active MOD, its invitation token, overrides, audit, and email.
    /// <summary>Verifies the complete successful ADM-M2 workflow and transaction boundary.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidRequest_ShouldPersistCommitEmailAndReturnCard()
    {
        // STEP-01: Declare addedUser to capture a call argument and 3 more; mock IAdministrationModRepository and 2 more; stub modRepository.EmailExistsAsync and 5 more.
        var modRepository = new Mock<IAdministrationModRepository>();
        User? addedUser = null;
        UserToken? addedToken = null;
        IReadOnlyList<UserPermission>? addedOverrides = null;
        modRepository.Setup(item => item.EmailExistsAsync("linh@angi.vn", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        modRepository.Setup(item => item.AddMod(It.IsAny<User>())).Callback<User>(user => addedUser = user);
        modRepository.Setup(item => item.AddPasswordResetToken(It.IsAny<UserToken>()))
            .Callback<UserToken>(token => addedToken = token);
        modRepository.Setup(item => item.AddPermissionOverrides(It.IsAny<IEnumerable<UserPermission>>()))
            .Callback<IEnumerable<UserPermission>>(items => addedOverrides = items.ToArray());
        var email = new Mock<IEmailService>();
        email.Setup(item => item.SendPasswordResetAsync(
            "linh@angi.vn", "Mod Linh", "raw-token", TimeSpan.FromMinutes(30), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var audit = new Mock<IAuditLogService>();
        AuditLogEntry? auditEntry = null;
        audit.Setup(item => item.Add(It.IsAny<AuditLogEntry>())).Callback<AuditLogEntry>(entry => auditEntry = entry);
        // STEP-02: Get transaction from Transaction and get unitOfWork from UnitOfWork.
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        // STEP-03: Build useCase under test through Create.
        var useCase = Create(modRepository.Object, email.Object, audit.Object, unitOfWork.Object);
        // STEP-04: Create request as CreateModRequestDto.
        var request = new CreateModRequestDto
        {
            Email = " Linh@ANGI.vn ",
            DisplayName = " Mod Linh ",
            PermissionOverrides = [new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "deny" }]
        };

        // STEP-05: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-06: Assert result is equivalent to the expected value, addedUser is not null, addedUser.EmailVerifiedAt is Now and addedUser.PasswordHash is null.
        result.Should().BeEquivalentTo(new
        {
            Email = "linh@angi.vn",
            DisplayName = "Mod Linh",
            Role = "MOD",
            Status = "active"
        });
        addedUser.Should().NotBeNull();
        addedUser!.EmailVerifiedAt.Should().Be(Now);
        addedUser.PasswordHash.Should().BeNull();
        // STEP-07: Assert addedToken.Purpose is UserTokenPurpose.PasswordReset and addedToken.ExpiresAt is Now.AddMinutes().
        addedToken!.Purpose.Should().Be(UserTokenPurpose.PasswordReset);
        addedToken.ExpiresAt.Should().Be(Now.AddMinutes(30));
        // STEP-08: Assert addedOverrides has exactly one matching item, addedOverrides matches the condition, auditEntry.Action is AuditActions.ModCreated and 1 more.
        addedOverrides.Should().ContainSingle().Which.Should().Match<UserPermission>(item =>
            item.PermissionId == 3 && item.Effect == PermissionEffect.Deny && item.GrantedBy == 7);
        auditEntry!.Action.Should().Be(AuditActions.ModCreated);
        auditEntry.SubjectUser.Should().BeSameAs(addedUser);
        // STEP-09: Verify SaveChangesAsync is called once and CommitAsync is called once.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        // STEP-10: Verify every setup on email ran.
        email.VerifyAll();
    }

    // TEST-02: Reject an email already owned by any non-deleted account.
    /// <summary>Verifies the early uniqueness check emits EMAIL_ALREADY_EXISTS without writes.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExistingEmail_ShouldThrowConflict()
    {
        // STEP-01: Mock IAdministrationModRepository; stub repository.EmailExistsAsync.
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.EmailExistsAsync("linh@angi.vn", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        // STEP-02: Build useCase under test through Create.
        var useCase = Create(repository.Object);

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-04: Assert act throws ConflictException and exception.ErrorCode is "EMAIL_ALREADY_EXISTS".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("EMAIL_ALREADY_EXISTS");
        // STEP-05: Verify AddMod is never called.
        repository.Verify(item => item.AddMod(It.IsAny<User>()), Times.Never);
    }

    // TEST-03: Translate a racing users-email unique violation to EMAIL_ALREADY_EXISTS.
    /// <summary>Verifies the database unique index remains authoritative under concurrent creation.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenEmailRaceOccurs_ShouldThrowConflict()
    {
        // STEP-01: Get repository from SuccessfulRepository, get transaction from Transaction and get unitOfWork from UnitOfWork.
        var repository = SuccessfulRepository();
        var transaction = Transaction();
        var unitOfWork = UnitOfWork(transaction);
        // STEP-02: Stub unitOfWork.SaveChangesAsync.
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UniqueConstraintViolationException("ix_users_email", new Exception()));
        // STEP-03: Build useCase under test through Create.
        var useCase = Create(repository.Object, unitOfWork: unitOfWork.Object);

        // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-05: Assert act throws ConflictException and exception.ErrorCode is "EMAIL_ALREADY_EXISTS".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("EMAIL_ALREADY_EXISTS");
        // STEP-06: Verify CommitAsync is never called.
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reject unknown permission codes before tracking the MOD.
    /// <summary>Verifies ADM-M2 uses the documented UNKNOWN_PERMISSION business error.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownPermission_ShouldThrowDocumentedError()
    {
        // STEP-01: Get repository from SuccessfulRepository, create request through ValidRequest and set request.PermissionOverrides to a test items.
        var repository = SuccessfulRepository();
        var request = ValidRequest();
        request.PermissionOverrides = [new PermissionOverrideDto { PermissionCode = "UNKNOWN", Effect = "allow" }];
        // STEP-02: Build useCase under test through Create.
        var useCase = Create(repository.Object);

        // STEP-03: Wrap useCase.ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-04: Assert act throws BadRequestException and exception.ErrorCode is "UNKNOWN_PERMISSION".
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("UNKNOWN_PERMISSION");
        // STEP-05: Verify AddMod is never called.
        repository.Verify(item => item.AddMod(It.IsAny<User>()), Times.Never);
    }

    // TEST-05: Reject both admin-only permissions with ADM-M2's listed error code.
    /// <summary>Verifies ADM-M2 cannot grant admin-only permissions or emit an unlisted endpoint error.</summary>
    [Theory]
    [InlineData("MOD_MANAGE")]
    [InlineData("SYSTEM_AUDIT_VIEW")]
    public async Task ExecuteAsync_WithAdminOnlyPermission_ShouldThrowUnknownPermission(string permissionCode)
    {
        // STEP-01: Get repository from SuccessfulRepository, create request through ValidRequest and set request.PermissionOverrides to a test items.
        var repository = SuccessfulRepository();
        var request = ValidRequest();
        request.PermissionOverrides = [new PermissionOverrideDto { PermissionCode = permissionCode, Effect = "allow" }];
        // STEP-02: Build useCase under test through Create.
        var useCase = Create(repository.Object);

        // STEP-03: Wrap useCase.ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-04: Assert act throws BadRequestException and exception.ErrorCode is "UNKNOWN_PERMISSION".
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("UNKNOWN_PERMISSION");
    }

    // TEST-06: Reject an unauthenticated invocation before opening a transaction.
    /// <summary>Verifies defense in depth for the required administration audit actor.</summary>
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

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-04: Assert act throws UnauthorizedException and exception.ErrorCode is "UNAUTHORIZED".
        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        // STEP-05: Verify unitOfWork receives no other calls.
        unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-07: Validate required fields before using authentication or persistence dependencies.
    /// <summary>Verifies validation is the first ADM-M2 operation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldStopBeforeDependencies()
    {
        // STEP-01: Mock IAdministrationModRepository and IUnitOfWork.
        var repository = new Mock<IAdministrationModRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-02: Build useCase under test through Create.
        var useCase = Create(repository.Object, unitOfWork: unitOfWork.Object);

        // STEP-03: Wrap useCase.ExecuteAsync with an empty CreateModRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(new CreateModRequestDto(), CancellationToken.None);

        // STEP-04: Assert act throws FluentValidation.ValidationException.
        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        // STEP-05: Verify repository receives no other calls and unitOfWork receives no other calls.
        repository.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
    }

    /// <summary>Creates a valid minimum ADM-M2 payload.</summary>
    private static CreateModRequestDto ValidRequest() => new()
    {
        Email = "linh@angi.vn",
        DisplayName = "Mod Linh"
    };

    /// <summary>Creates a MOD repository mock that passes email uniqueness and accepts tracked entities.</summary>
    private static Mock<IAdministrationModRepository> SuccessfulRepository()
    {
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.EmailExistsAsync("linh@angi.vn", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return repository;
    }

    /// <summary>Creates the system under test with deterministic defaults and the real validator.</summary>
    private static CreateModUseCase Create(
        IAdministrationModRepository modRepository,
        IEmailService? email = null,
        IAuditLogService? audit = null,
        IUnitOfWork? unitOfWork = null,
        ICurrentUserService? currentUser = null)
    {
        var permissions = new Mock<IAdministrationPermissionRepository>();
        permissions.Setup(item => item.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PermissionCatalogSnapshot(
                3,
                [
                    new(3, "USER_BAN", "Ban"),
                    new(11, "MOD_MANAGE", "Manage Mods"),
                    new(12, "SYSTEM_AUDIT_VIEW", "Audit")
                ],
                ["USER_BAN"]));
        var tokenService = new Mock<IAuthenticationTokenService>();
        tokenService.Setup(item => item.CreateUserToken()).Returns(new GeneratedUserToken("raw-token", "token-hash"));
        currentUser ??= CurrentUser();
        return new CreateModUseCase(
            new CreateModRequestDtoValidator(),
            modRepository,
            permissions.Object,
            tokenService.Object,
            email ?? Email(),
            currentUser,
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

    /// <summary>Creates a successful no-op invitation email service.</summary>
    private static IEmailService Email()
    {
        var email = new Mock<IEmailService>();
        email.Setup(item => item.SendPasswordResetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return email.Object;
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

    /// <summary>Clock fixed at one instant so creation and invitation expiry timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        /// <summary>Returns the deterministic creation and expiration timestamp.</summary>
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
