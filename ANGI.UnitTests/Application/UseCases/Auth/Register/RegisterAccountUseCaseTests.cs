using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.Register;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Auth.Register;

/// <summary>Verifies RegisterAccountUseCase creates pending accounts with a verification token and sends email only after commit.</summary>
public sealed class RegisterAccountUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 4, 0, 0, DateTimeKind.Utc);

    // TEST-01: Create a pending account, hashed verification token, audit entry, and verification email.
    /// <summary>Verifies the complete successful Traveler registration workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidTraveler_ShouldCreatePendingAccountAndSendVerificationEmail()
    {
        // STEP-01: Create operations as List<string>.
        var operations = new List<string>();
        // STEP-02: Declare addedUser to capture a call argument and 3 more; mock IAuthenticationRepository and 5 more; stub repository.EmailExistsAsync and 10 more.
        var repository = new Mock<IAuthenticationRepository>();
        repository.Setup(x => x.EmailExistsAsync("an.nguyen@gmail.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" });
        User? addedUser = null;
        UserToken? addedToken = null;
        repository.Setup(x => x.AddUser(It.IsAny<User>())).Callback<User>(user => addedUser = user);
        repository.Setup(x => x.AddUserToken(It.IsAny<UserToken>())).Callback<UserToken>(token => addedToken = token);

        var passwordService = new Mock<IPasswordService>();
        passwordService.Setup(x => x.Hash("MatKhau123")).Returns("password-hash");
        var tokenService = new Mock<IAuthenticationTokenService>();
        tokenService.Setup(x => x.CreateUserToken())
            .Returns(new GeneratedUserToken("raw-verification-token", "verification-hash"));
        var emailService = new Mock<IEmailService>();
        emailService.Setup(x => x.SendEmailVerificationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("email"))
            .Returns(Task.CompletedTask);
        var auditService = new Mock<IAuditLogService>();
        AuditLogEntry? auditEntry = null;
        auditService.Setup(x => x.Add(It.IsAny<AuditLogEntry>()))
            .Callback<AuditLogEntry>(entry => auditEntry = entry);
        var transaction = CreateTransaction();
        transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("commit"))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                operations.Add("save");
                addedUser!.Id = 12;
            })
            .ReturnsAsync(3);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            passwordService,
            tokenService,
            emailService,
            auditService,
            unitOfWork);

        // STEP-04: Run useCase.ExecuteAsync with RegisterAccountRequestDto { Email, Password, DisplayName, ... } and keep result.
        var result = await useCase.ExecuteAsync(new RegisterAccountRequestDto
        {
            Email = "An.Nguyen@GMAIL.com",
            Password = "MatKhau123",
            DisplayName = " Nguyễn An ",
            Role = "TRAVELER"
        }, CancellationToken.None);

        // STEP-05: Assert addedUser is not null, addedUser.Email is "an.nguyen@gmail.com", addedUser.PasswordHash is a test value and 2 more.
        addedUser.Should().NotBeNull();
        addedUser!.Email.Should().Be("an.nguyen@gmail.com");
        addedUser.PasswordHash.Should().Be("password-hash");
        addedUser.DisplayName.Should().Be("Nguyễn An");
        addedUser.Status.Should().Be(UserStatus.PendingVerification);
        // STEP-06: Assert addedToken is not null, addedToken.User is the same instance as addedUser, addedToken.Purpose is UserTokenPurpose.EmailVerify and 1 more.
        addedToken.Should().NotBeNull();
        addedToken!.User.Should().BeSameAs(addedUser);
        addedToken.Purpose.Should().Be(UserTokenPurpose.EmailVerify);
        addedToken.TokenHash.Should().Be("verification-hash");
        // STEP-07: Assert addedToken.ExpiresAt is Now.AddHours(), auditEntry is not null, auditEntry.Action is AuditActions.UserRegistered and 3 more.
        addedToken.ExpiresAt.Should().Be(Now.AddHours(24));
        auditEntry.Should().NotBeNull();
        auditEntry!.Action.Should().Be(AuditActions.UserRegistered);
        auditEntry.ActorUser.Should().BeSameAs(addedUser);
        auditEntry.SubjectUser.Should().BeSameAs(addedUser);
        auditEntry.ActorRole.Should().Be("TRAVELER");
        // STEP-08: Assert result.UserId is 12, result.Email is "an.nguyen@gmail.com", result.Role is "TRAVELER" and result.Status is "pending_verification".
        result.UserId.Should().Be(12);
        result.Email.Should().Be("an.nguyen@gmail.com");
        result.Role.Should().Be("TRAVELER");
        result.Status.Should().Be("pending_verification");
        // STEP-09: Assert operations equals "save".
        operations.Should().Equal("save", "commit", "email");
        // STEP-10: Verify SendEmailVerificationAsync is called once, SaveChangesAsync is called once and CommitAsync is called once.
        emailService.Verify(x => x.SendEmailVerificationAsync(
            "an.nguyen@gmail.com",
            "Nguyễn An",
            "raw-verification-token",
            TimeSpan.FromHours(24),
            It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Support the second self-registration role through the complete use-case path.
    /// <summary>Ensures restaurant owners can self-register and receive the owner role.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidRestaurantOwner_ShouldAssignOwnerRole()
    {
        // STEP-01: Create transaction through CreateTransaction; mock IAuthenticationRepository and 3 more; stub repository.EmailExistsAsync and 5 more.
        var repository = new Mock<IAuthenticationRepository>();
        repository.Setup(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(x => x.GetRoleByCodeAsync("RESTAURANT_OWNER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Id = 2, Code = "RESTAURANT_OWNER", Name = "Restaurant Owner" });
        var transaction = CreateTransaction();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var passwordService = new Mock<IPasswordService>();
        passwordService.Setup(x => x.Hash(It.IsAny<string>())).Returns("password-hash");
        var tokenService = new Mock<IAuthenticationTokenService>();
        tokenService.Setup(x => x.CreateUserToken())
            .Returns(new GeneratedUserToken("raw-token", "token-hash"));
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            passwordService,
            tokenService,
            new Mock<IEmailService>(),
            new Mock<IAuditLogService>(),
            unitOfWork);
        // STEP-03: Create request through ValidRequest and set request.Role to "RESTAURANT_OWNER".
        var request = ValidRequest();
        request.Role = "RESTAURANT_OWNER";

        // STEP-04: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-05: Assert result.Role is "RESTAURANT_OWNER".
        result.Role.Should().Be("RESTAURANT_OWNER");
        // STEP-06: Verify GetRoleByCodeAsync is called once.
        repository.Verify(x => x.GetRoleByCodeAsync("RESTAURANT_OWNER", It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Return EMAIL_ALREADY_EXISTS without hashing, saving, or sending email for a used email.
    /// <summary>Verifies that duplicate email detection stops every registration side effect.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExistingEmail_ShouldThrowConflictWithoutWrites()
    {
        // STEP-01: Mock IAuthenticationRepository, IPasswordService, IEmailService and IUnitOfWork; stub repository.EmailExistsAsync and unitOfWork.BeginTransactionAsync.
        var repository = new Mock<IAuthenticationRepository>();
        repository.Setup(x => x.EmailExistsAsync("used@angi.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var passwordService = new Mock<IPasswordService>();
        var emailService = new Mock<IEmailService>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTransaction().Object);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            passwordService,
            new Mock<IAuthenticationTokenService>(),
            emailService,
            new Mock<IAuditLogService>(),
            unitOfWork);

        // STEP-03: Create request through ValidRequest and set request.Email to "USED@angi.test".
        var request = ValidRequest();
        request.Email = "USED@angi.test";

        // STEP-04: Wrap useCase.ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-05: Assert act throws ConflictException and exception.ErrorCode is "EMAIL_ALREADY_EXISTS".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("EMAIL_ALREADY_EXISTS");
        // STEP-06: Verify Hash is never called, AddUser is never called, SaveChangesAsync is never called and SendEmailVerificationAsync is never called.
        passwordService.Verify(x => x.Hash(It.IsAny<string>()), Times.Never);
        repository.Verify(x => x.AddUser(It.IsAny<User>()), Times.Never);
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        emailService.Verify(x => x.SendEmailVerificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Map the database constraint race to the documented conflict response.
    /// <summary>Ensures a lost registration race returns the documented conflict and sends no email.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenConcurrentInsertWins_ShouldThrowEmailAlreadyExistsWithoutEmail()
    {
        // STEP-01: Create transaction through CreateTransaction; mock IAuthenticationRepository and 4 more; stub repository.EmailExistsAsync and 5 more.
        var repository = new Mock<IAuthenticationRepository>();
        repository.Setup(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" });
        var passwordService = new Mock<IPasswordService>();
        passwordService.Setup(x => x.Hash(It.IsAny<string>())).Returns("password-hash");
        var tokenService = new Mock<IAuthenticationTokenService>();
        tokenService.Setup(x => x.CreateUserToken())
            .Returns(new GeneratedUserToken("raw-token", "token-hash"));
        var emailService = new Mock<IEmailService>();
        var transaction = CreateTransaction();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UniqueConstraintViolationException("ix_users_email", new Exception("duplicate")));
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            passwordService,
            tokenService,
            emailService,
            new Mock<IAuditLogService>(),
            unitOfWork);

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-04: Assert act throws ConflictException and exception.ErrorCode is "EMAIL_ALREADY_EXISTS".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("EMAIL_ALREADY_EXISTS");
        // STEP-05: Verify CommitAsync is never called and SendEmailVerificationAsync is never called.
        transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        emailService.Verify(x => x.SendEmailVerificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: A failed commit must never leak a verification link for data that was not committed.
    /// <summary>Prevents a verification link from being sent for an account that was never saved.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenCommitFails_ShouldNotSendVerificationEmail()
    {
        // STEP-01: Get repository from SuccessfulRepository, get passwordService from SuccessfulPasswordService and get tokenService from SuccessfulTokenService.
        var repository = SuccessfulRepository();
        var passwordService = SuccessfulPasswordService();
        var tokenService = SuccessfulTokenService();
        // STEP-02: Create transaction through CreateTransaction; mock IEmailService and 1 more; stub transaction.CommitAsync and 2 more.
        var emailService = new Mock<IEmailService>();
        var transaction = CreateTransaction();
        transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("commit failed"));
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            passwordService,
            tokenService,
            emailService,
            new Mock<IAuditLogService>(),
            unitOfWork);

        // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-05: Assert act throws InvalidOperationException.
        await act.Should().ThrowAsync<InvalidOperationException>();
        // STEP-06: Verify SendEmailVerificationAsync is never called.
        emailService.Verify(x => x.SendEmailVerificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Email delivery failure happens only after the account and token are durable.
    /// <summary>Ensures an email failure surfaces only after the account and token are durable.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenEmailFails_ShouldHaveCommittedBeforePropagatingFailure()
    {
        // STEP-01: Get repository from SuccessfulRepository.
        var repository = SuccessfulRepository();
        // STEP-02: Create transaction through CreateTransaction; mock IEmailService and 1 more; stub emailService.SendEmailVerificationAsync and 2 more.
        var emailService = new Mock<IEmailService>();
        emailService.Setup(x => x.SendEmailVerificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceUnavailableException("SERVICE_UNAVAILABLE", "Email unavailable."));
        var transaction = CreateTransaction();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            SuccessfulPasswordService(),
            SuccessfulTokenService(),
            emailService,
            new Mock<IAuditLogService>(),
            unitOfWork);

        // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-05: Assert act throws ServiceUnavailableException.
        await act.Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-06: Verify CommitAsync is called once.
        transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-07: Validate the request before opening a transaction or querying persistence.
    /// <summary>Verifies that FluentValidation remains the first operation in the use case.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldFailValidationFirst()
    {
        // STEP-01: Mock IAuthenticationRepository and IUnitOfWork.
        var repository = new Mock<IAuthenticationRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            new Mock<IPasswordService>(),
            new Mock<IAuthenticationTokenService>(),
            new Mock<IEmailService>(),
            new Mock<IAuditLogService>(),
            unitOfWork);

        // STEP-03: Wrap useCase.ExecuteAsync with an empty RegisterAccountRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(new RegisterAccountRequestDto(), CancellationToken.None);

        // STEP-04: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
        // STEP-05: Verify BeginTransactionAsync is never called and EmailExistsAsync is never called.
        unitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Builds the use case with real AUTH-01 validation and mocked boundaries.</summary>
    private static RegisterAccountUseCase CreateUseCase(
        Mock<IAuthenticationRepository> repository,
        Mock<IPasswordService> passwordService,
        Mock<IAuthenticationTokenService> tokenService,
        Mock<IEmailService> emailService,
        Mock<IAuditLogService> auditService,
        Mock<IUnitOfWork> unitOfWork)
    {
        return new RegisterAccountUseCase(
            new RegisterAccountRequestDtoValidator(),
            repository.Object,
            passwordService.Object,
            tokenService.Object,
            emailService.Object,
            auditService.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));
    }

    /// <summary>Creates a valid registration request that individual tests can vary.</summary>
    private static RegisterAccountRequestDto ValidRequest() => new()
    {
        Email = "used@angi.test",
        Password = "MatKhau123",
        DisplayName = "Nguyễn An",
        Role = "TRAVELER"
    };

    /// <summary>Creates a repository mock where the email is free and the TRAVELER role exists.</summary>
    private static Mock<IAuthenticationRepository> SuccessfulRepository()
    {
        var repository = new Mock<IAuthenticationRepository>();
        repository.Setup(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" });
        return repository;
    }

    /// <summary>Creates a password service mock that returns a fixed hash.</summary>
    private static Mock<IPasswordService> SuccessfulPasswordService()
    {
        var service = new Mock<IPasswordService>();
        service.Setup(x => x.Hash(It.IsAny<string>())).Returns("password-hash");
        return service;
    }

    /// <summary>Creates a token service mock that returns a fixed raw token and hash pair.</summary>
    private static Mock<IAuthenticationTokenService> SuccessfulTokenService()
    {
        var service = new Mock<IAuthenticationTokenService>();
        service.Setup(x => x.CreateUserToken())
            .Returns(new GeneratedUserToken("raw-token", "token-hash"));
        return service;
    }

    /// <summary>Creates a disposable transaction mock for registration tests.</summary>
    private static Mock<IUnitOfWorkTransaction> CreateTransaction()
    {
        var transaction = new Mock<IUnitOfWorkTransaction>();
        transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        transaction.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return transaction;
    }

    /// <summary>Clock fixed at one instant so token expiry can be asserted exactly.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        /// <summary>Initializes the deterministic UTC clock used by the test.</summary>
        public FixedTimeProvider(DateTime now)
        {
            _now = new DateTimeOffset(now);
        }

        /// <summary>Returns the fixed instant for every call.</summary>
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
