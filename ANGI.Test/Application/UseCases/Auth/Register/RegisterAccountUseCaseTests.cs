using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.Register;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.Test.Application.UseCases.Auth.Register;

public sealed class RegisterAccountUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 4, 0, 0, DateTimeKind.Utc);

    // TEST-01: Create a pending account, hashed verification token, audit entry, and verification email.
    /// <summary>Verifies the complete successful Traveler registration workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidTraveler_ShouldCreatePendingAccountAndSendVerificationEmail()
    {
        var operations = new List<string>();
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
        tokenService.Setup(x => x.CreateUserToken()).Returns("raw-verification-token");
        tokenService.Setup(x => x.HashUserToken("raw-verification-token")).Returns("verification-hash");
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
        var useCase = CreateUseCase(
            repository,
            passwordService,
            tokenService,
            emailService,
            auditService,
            unitOfWork);

        var result = await useCase.ExecuteAsync(new RegisterAccountRequestDto
        {
            Email = "An.Nguyen@GMAIL.com",
            Password = "MatKhau123",
            DisplayName = " Nguyễn An ",
            Role = "TRAVELER"
        }, CancellationToken.None);

        addedUser.Should().NotBeNull();
        addedUser!.Email.Should().Be("an.nguyen@gmail.com");
        addedUser.PasswordHash.Should().Be("password-hash");
        addedUser.DisplayName.Should().Be("Nguyễn An");
        addedUser.Status.Should().Be(UserStatus.PendingVerification);
        addedToken.Should().NotBeNull();
        addedToken!.User.Should().BeSameAs(addedUser);
        addedToken.Purpose.Should().Be(UserTokenPurpose.EmailVerify);
        addedToken.TokenHash.Should().Be("verification-hash");
        addedToken.ExpiresAt.Should().Be(Now.AddHours(24));
        auditEntry.Should().NotBeNull();
        auditEntry!.Action.Should().Be(AuditActions.UserRegistered);
        auditEntry.ActorUser.Should().BeSameAs(addedUser);
        auditEntry.SubjectUser.Should().BeSameAs(addedUser);
        auditEntry.ActorRole.Should().Be("TRAVELER");
        result.UserId.Should().Be(12);
        result.Email.Should().Be("an.nguyen@gmail.com");
        result.Role.Should().Be("TRAVELER");
        result.Status.Should().Be("pending_verification");
        operations.Should().Equal("save", "commit", "email");
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
    [Fact]
    public async Task ExecuteAsync_WithValidRestaurantOwner_ShouldAssignOwnerRole()
    {
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
        tokenService.Setup(x => x.CreateUserToken()).Returns("raw-token");
        tokenService.Setup(x => x.HashUserToken("raw-token")).Returns("token-hash");
        var useCase = CreateUseCase(
            repository,
            passwordService,
            tokenService,
            new Mock<IEmailService>(),
            new Mock<IAuditLogService>(),
            unitOfWork);
        var request = ValidRequest();
        request.Role = "RESTAURANT_OWNER";

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Role.Should().Be("RESTAURANT_OWNER");
        repository.Verify(x => x.GetRoleByCodeAsync("RESTAURANT_OWNER", It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Return EMAIL_ALREADY_EXISTS without hashing, saving, or sending email for a used email.
    /// <summary>Verifies that duplicate email detection stops every registration side effect.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExistingEmail_ShouldThrowConflictWithoutWrites()
    {
        var repository = new Mock<IAuthenticationRepository>();
        repository.Setup(x => x.EmailExistsAsync("used@angi.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var passwordService = new Mock<IPasswordService>();
        var emailService = new Mock<IEmailService>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTransaction().Object);
        var useCase = CreateUseCase(
            repository,
            passwordService,
            new Mock<IAuthenticationTokenService>(),
            emailService,
            new Mock<IAuditLogService>(),
            unitOfWork);

        var request = ValidRequest();
        request.Email = "USED@angi.test";

        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("EMAIL_ALREADY_EXISTS");
        passwordService.Verify(x => x.Hash(It.IsAny<string>()), Times.Never);
        repository.Verify(x => x.AddUser(It.IsAny<User>()), Times.Never);
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        emailService.Verify(x => x.SendEmailVerificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Map the database constraint race to the documented conflict response.
    [Fact]
    public async Task ExecuteAsync_WhenConcurrentInsertWins_ShouldThrowEmailAlreadyExistsWithoutEmail()
    {
        var repository = new Mock<IAuthenticationRepository>();
        repository.Setup(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" });
        var passwordService = new Mock<IPasswordService>();
        passwordService.Setup(x => x.Hash(It.IsAny<string>())).Returns("password-hash");
        var tokenService = new Mock<IAuthenticationTokenService>();
        tokenService.Setup(x => x.CreateUserToken()).Returns("raw-token");
        tokenService.Setup(x => x.HashUserToken("raw-token")).Returns("token-hash");
        var emailService = new Mock<IEmailService>();
        var transaction = CreateTransaction();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UniqueConstraintViolationException("ix_users_email", new Exception("duplicate")));
        var useCase = CreateUseCase(
            repository,
            passwordService,
            tokenService,
            emailService,
            new Mock<IAuditLogService>(),
            unitOfWork);

        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("EMAIL_ALREADY_EXISTS");
        transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        emailService.Verify(x => x.SendEmailVerificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: A failed commit must never leak a verification link for data that was not committed.
    [Fact]
    public async Task ExecuteAsync_WhenCommitFails_ShouldNotSendVerificationEmail()
    {
        var repository = SuccessfulRepository();
        var passwordService = SuccessfulPasswordService();
        var tokenService = SuccessfulTokenService();
        var emailService = new Mock<IEmailService>();
        var transaction = CreateTransaction();
        transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("commit failed"));
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var useCase = CreateUseCase(
            repository,
            passwordService,
            tokenService,
            emailService,
            new Mock<IAuditLogService>(),
            unitOfWork);

        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        emailService.Verify(x => x.SendEmailVerificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Email delivery failure happens only after the account and token are durable.
    [Fact]
    public async Task ExecuteAsync_WhenEmailFails_ShouldHaveCommittedBeforePropagatingFailure()
    {
        var repository = SuccessfulRepository();
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
        var useCase = CreateUseCase(
            repository,
            SuccessfulPasswordService(),
            SuccessfulTokenService(),
            emailService,
            new Mock<IAuditLogService>(),
            unitOfWork);

        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<ServiceUnavailableException>();
        transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-07: Validate the request before opening a transaction or querying persistence.
    /// <summary>Verifies that FluentValidation remains the first operation in the use case.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldFailValidationFirst()
    {
        var repository = new Mock<IAuthenticationRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(
            repository,
            new Mock<IPasswordService>(),
            new Mock<IAuthenticationTokenService>(),
            new Mock<IEmailService>(),
            new Mock<IAuditLogService>(),
            unitOfWork);

        var act = () => useCase.ExecuteAsync(new RegisterAccountRequestDto(), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
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

    private static Mock<IAuthenticationRepository> SuccessfulRepository()
    {
        var repository = new Mock<IAuthenticationRepository>();
        repository.Setup(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" });
        return repository;
    }

    private static Mock<IPasswordService> SuccessfulPasswordService()
    {
        var service = new Mock<IPasswordService>();
        service.Setup(x => x.Hash(It.IsAny<string>())).Returns("password-hash");
        return service;
    }

    private static Mock<IAuthenticationTokenService> SuccessfulTokenService()
    {
        var service = new Mock<IAuthenticationTokenService>();
        service.Setup(x => x.CreateUserToken()).Returns("raw-token");
        service.Setup(x => x.HashUserToken("raw-token")).Returns("token-hash");
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
