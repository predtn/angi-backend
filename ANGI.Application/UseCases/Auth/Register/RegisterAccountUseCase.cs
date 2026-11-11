using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.Register;

/// <summary>Implements AUTH-01 by creating a pending account and its email verification token.</summary>
public sealed class RegisterAccountUseCase : IRegisterAccountUseCase
{
    private const string UsersEmailUniqueConstraint = "ix_users_email";
    private const string EmailAlreadyExistsCode = "EMAIL_ALREADY_EXISTS";
    private const string EmailAlreadyExistsMessage = "Email đã được sử dụng.";
    private static readonly TimeSpan _emailVerificationLifetime = TimeSpan.FromHours(24);

    private readonly IValidator<RegisterAccountRequestDto> _validator;
    private readonly IAuthenticationRepository _authenticationRepository;
    private readonly IPasswordService _passwordService;
    private readonly IAuthenticationTokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the registration workflow and its validation, persistence, and email dependencies.</summary>
    public RegisterAccountUseCase(
        IValidator<RegisterAccountRequestDto> validator,
        IAuthenticationRepository authenticationRepository,
        IPasswordService passwordService,
        IAuthenticationTokenService tokenService,
        IEmailService emailService,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _authenticationRepository = authenticationRepository;
        _passwordService = passwordService;
        _tokenService = tokenService;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Validates uniqueness, persists the pending account atomically, and sends its verification link.</summary>
    public async Task<RegisterAccountResponseDto> ExecuteAsync(
        RegisterAccountRequestDto request,
        CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare normalizedEmail through request.Email.Trim().ToLowerInvariant.
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

        // STEP-04: Throw the error from EmailAlreadyExists when _authenticationRepository.EmailExistsAsync() is true.
        if (await _authenticationRepository.EmailExistsAsync(normalizedEmail, ct))
        {
            throw EmailAlreadyExists();
        }

        // STEP-05: Prepare role from authenticationRepository.GetRoleByCodeAsync, generatedVerificationToken through tokenService.CreateUserToken, user as User and userToken as UserToken.
        var role = await _authenticationRepository.GetRoleByCodeAsync(request.Role, ct)
            ?? throw new InvalidOperationException($"Configured role '{request.Role}' was not found.");
        var generatedVerificationToken = _tokenService.CreateUserToken();
        var user = new User
        {
            Email = normalizedEmail,
            RoleId = role.Id,
            PasswordHash = _passwordService.Hash(request.Password),
            DisplayName = request.DisplayName.Trim(),
            Status = UserStatus.PendingVerification
        };
        var userToken = new UserToken
        {
            User = user,
            Purpose = UserTokenPurpose.EmailVerify,
            TokenHash = generatedVerificationToken.TokenHash,
            ExpiresAt = _timeProvider.GetUtcNow().UtcDateTime.Add(_emailVerificationLifetime)
        };

        // STEP-06: Queue user through authenticationRepository.AddUser and queue userToken through authenticationRepository.AddUserToken.
        _authenticationRepository.AddUser(user);
        _authenticationRepository.AddUserToken(userToken);
        // STEP-07: Record audit action AuditActions.UserRegistered.
        _auditLogService.Add(new AuditLogEntry
        {
            Action = AuditActions.UserRegistered,
            EntityType = AuditEntityTypes.User,
            ActorUser = user,
            ActorRole = role.Code,
            SubjectUser = user
        });

        try
        {
            // STEP-08: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);
            // STEP-09: Commit the transaction.
            await transaction.CommitAsync(ct);
        }
        catch (UniqueConstraintViolationException exception) when (
            string.Equals(exception.ConstraintName, UsersEmailUniqueConstraint, StringComparison.Ordinal))
        {
            // STEP-10: On UniqueConstraintViolationException when exception.ConstraintName equals UsersEmailUniqueConstraint, throw the error from EmailAlreadyExists.
            // The initial read is only an early check. The unique index remains authoritative
            // when two registrations for the same normalized email race each other.
            throw EmailAlreadyExists();
        }

        // STEP-11: Call emailService.SendEmailVerificationAsync.
        // Do not send a verification link until its user and token are durably committed.
        // If delivery fails, AUTH-03 can issue a fresh token for the committed account.
        await _emailService.SendEmailVerificationAsync(
            user.Email,
            user.DisplayName,
            generatedVerificationToken.Token,
            _emailVerificationLifetime,
            ct);

        // STEP-12: Return a new RegisterAccountResponseDto.
        return new RegisterAccountResponseDto
        {
            UserId = user.Id,
            Email = user.Email,
            Role = role.Code,
            Status = JsonNamingPolicy.SnakeCaseLower.ConvertName(user.Status.ToString())
        };
    }

    private static ConflictException EmailAlreadyExists() =>
        new(EmailAlreadyExistsCode, EmailAlreadyExistsMessage);
}
