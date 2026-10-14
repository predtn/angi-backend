using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Account;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Account.ChangePassword;

/// <summary>Coordinates password changing and other sessions revocation for the authenticated user (ME-03).</summary>
public sealed class ChangePasswordUseCase : IChangePasswordUseCase
{
    private const string WrongCurrentPasswordCode = "WRONG_CURRENT_PASSWORD";
    private const string WrongCurrentPasswordMessage = "Mật khẩu hiện tại không chính xác.";
    private const string ValidationFailedCode = "VALIDATION_FAILED";
    private const string SamePasswordMessage = "Mật khẩu mới không được trùng với mật khẩu cũ.";
    private const string UnauthorizedCode = "UNAUTHORIZED";
    private const string UnauthorizedMessage = "Yêu cầu đăng nhập.";

    private readonly IValidator<ChangePasswordRequestDto> _validator;
    private readonly IAccountRepository _accountRepository;
    private readonly IPasswordService _passwordService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ChangePasswordUseCase(
        IValidator<ChangePasswordRequestDto> validator,
        IAccountRepository accountRepository,
        IPasswordService passwordService,
        ICurrentUserService currentUserService,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _accountRepository = accountRepository;
        _passwordService = passwordService;
        _currentUserService = currentUserService;
        _auditLogService = auditLogService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task ExecuteAsync(ChangePasswordRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId;
        if (userId is null)
        {
            throw new UnauthorizedException(UnauthorizedCode, UnauthorizedMessage);
        }

        var user = await _accountRepository.GetUserByIdAsync(userId.Value, ct);
        if (user is null)
        {
            throw new UnauthorizedException(UnauthorizedCode, UnauthorizedMessage);
        }

        var currentPassword = request.CurrentPassword ?? request.OldPassword;

        if (user.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(currentPassword) || !_passwordService.Verify(currentPassword, user.PasswordHash))
            {
                throw new BadRequestException(WrongCurrentPasswordCode, WrongCurrentPasswordMessage);
            }

            if (_passwordService.Verify(request.NewPassword, user.PasswordHash))
            {
                throw new BadRequestException(ValidationFailedCode, SamePasswordMessage);
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        user.PasswordHash = _passwordService.Hash(request.NewPassword);

        await RevokeOtherSessionsAsync(user.Id, now, ct);

        _auditLogService.Add(new AuditLogEntry
        {
            Action = AuditActions.PasswordChanged,
            EntityType = AuditEntityTypes.User,
            EntityId = user.Id,
            SubjectUserId = user.Id
        });

        await _unitOfWork.SaveChangesAsync(ct);
    }

    private async Task RevokeOtherSessionsAsync(int userId, DateTime now, CancellationToken ct)
    {
        var activeSessions = await _accountRepository.GetActiveSessionsAsync(userId, ct);
        if (activeSessions.Count == 0)
        {
            return;
        }

        UserSession? currentSession = null;

        if (!string.IsNullOrEmpty(_currentUserService.UserAgent))
        {
            currentSession = activeSessions
                .Where(s => string.Equals(s.UserAgent, _currentUserService.UserAgent, StringComparison.Ordinal))
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault();
        }

        if (currentSession is null && _currentUserService.IpAddress is not null)
        {
            currentSession = activeSessions
                .Where(s => s.IpAddress is not null && s.IpAddress.Equals(_currentUserService.IpAddress))
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault();
        }

        if (currentSession is null)
        {
            currentSession = activeSessions.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        }

        foreach (var session in activeSessions)
        {
            if (currentSession is not null && session.Id == currentSession.Id)
            {
                continue;
            }

            session.RevokedAt = now;
        }
    }
}
