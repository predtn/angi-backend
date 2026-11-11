using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.CreateMod;

/// <summary>Implements ADM-M2 by creating and inviting a MOD account.</summary>
public sealed class CreateModUseCase : ICreateModUseCase
{
    private const string UsersEmailUniqueConstraint = "ix_users_email";
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromMinutes(30);

    private readonly IValidator<CreateModRequestDto> _validator;
    private readonly IAdministrationModRepository _modRepository;
    private readonly IAdministrationPermissionRepository _permissionRepository;
    private readonly IAuthenticationTokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes validation, persistence, token, email, audit, transaction, and time dependencies.</summary>
    public CreateModUseCase(
        IValidator<CreateModRequestDto> validator,
        IAdministrationModRepository modRepository,
        IAdministrationPermissionRepository permissionRepository,
        IAuthenticationTokenService tokenService,
        IEmailService emailService,
        ICurrentUserService currentUserService,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _modRepository = modRepository;
        _permissionRepository = permissionRepository;
        _tokenService = tokenService;
        _emailService = emailService;
        _currentUserService = currentUserService;
        _auditLogService = auditLogService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Creates the active verified MOD atomically, then sends its AUTH-09 password setup link.</summary>
    public async Task<AdminUserCardDto> ExecuteAsync(CreateModRequestDto request, CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare actorId, normalizedEmail through request.Email.Trim().ToLowerInvariant, permissionOverrides, generatedToken through tokenService.CreateUserToken and 1 more.
        var actorId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Yêu cầu đăng nhập.");
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var permissionOverrides = request.PermissionOverrides ?? [];
        var generatedToken = _tokenService.CreateUserToken();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Throw the error from EmailAlreadyExists when _modRepository.EmailExistsAsync() is true.
        if (await _modRepository.EmailExistsAsync(normalizedEmail, ct))
        {
            throw EmailAlreadyExists();
        }

        // STEP-05: Load catalog through permissionRepository.GetCatalogAsync.
        var catalog = await _permissionRepository.GetCatalogAsync(ct);
        // STEP-06: Prepare permissionByCode through AdministrationPermissionRules.ResolveGrantable, user as User and persistedOverrides through Select(filtered).ToArray.
        var permissionByCode = AdministrationPermissionRules.ResolveGrantable(
            permissionOverrides.Select(item => item.PermissionCode),
            catalog,
            "UNKNOWN_PERMISSION");
        var user = new User
        {
            Email = normalizedEmail,
            RoleId = catalog.ModRoleId,
            DisplayName = request.DisplayName.Trim(),
            EmailVerifiedAt = now,
            Status = UserStatus.Active
        };
        var persistedOverrides = permissionOverrides.Select(item => new UserPermission
        {
            User = user,
            PermissionId = permissionByCode[item.PermissionCode].Id,
            Effect = item.Effect == "allow" ? PermissionEffect.Allow : PermissionEffect.Deny,
            GrantedBy = actorId,
            GrantedAt = now
        }).ToArray();

        // STEP-07: Queue user through modRepository.AddMod, queue a new UserToken through modRepository.AddPasswordResetToken and more.
        _modRepository.AddMod(user);
        _modRepository.AddPasswordResetToken(new UserToken
        {
            User = user,
            Purpose = UserTokenPurpose.PasswordReset,
            TokenHash = generatedToken.TokenHash,
            ExpiresAt = now.Add(InvitationLifetime)
        });
        _modRepository.AddPermissionOverrides(persistedOverrides);
        // STEP-08: Record audit action AuditActions.ModCreated.
        _auditLogService.Add(new AuditLogEntry
        {
            Action = AuditActions.ModCreated,
            EntityType = AuditEntityTypes.User,
            SubjectUser = user,
            NewValues = new
            {
                user.Email,
                user.DisplayName,
                Status = UserStatus.Active,
                PermissionOverrides = permissionOverrides
            }
        });

        try
        {
            // STEP-09: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);
            // STEP-10: Commit the transaction.
            await transaction.CommitAsync(ct);
        }
        catch (UniqueConstraintViolationException exception) when (
            string.Equals(exception.ConstraintName, UsersEmailUniqueConstraint, StringComparison.Ordinal))
        {
            // STEP-11: On UniqueConstraintViolationException when exception.ConstraintName equals UsersEmailUniqueConstraint, throw the error from EmailAlreadyExists.
            throw EmailAlreadyExists();
        }

        // STEP-12: Call emailService.SendPasswordResetAsync.
        await _emailService.SendPasswordResetAsync(
            user.Email,
            user.DisplayName,
            generatedToken.Token,
            InvitationLifetime,
            ct);

        // STEP-13: Return the result of AdministrationModMapper.ToCardDto.
        return AdministrationModMapper.ToCardDto(user, "MOD");
    }

    /// <summary>Creates the documented duplicate-email conflict.</summary>
    private static ConflictException EmailAlreadyExists() =>
        new("EMAIL_ALREADY_EXISTS", "Email đã được sử dụng.");
}
