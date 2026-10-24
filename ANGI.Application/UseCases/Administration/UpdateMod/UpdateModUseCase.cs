using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.UpdateMod;

/// <summary>Implements ADM-M4 MOD account profile and activation-state changes.</summary>
public sealed class UpdateModUseCase : IUpdateModUseCase
{
    private readonly IValidator<UpdateModRequestDto> _validator;
    private readonly IAdministrationModRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAccountStatusCache _accountStatusCache;
    private readonly IAuditLogService _auditLogService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes validation, persistence, identity, cache, audit, transaction, and time dependencies.</summary>
    public UpdateModUseCase(
        IValidator<UpdateModRequestDto> validator,
        IAdministrationModRepository repository,
        ICurrentUserService currentUserService,
        IAccountStatusCache accountStatusCache,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _repository = repository;
        _currentUserService = currentUserService;
        _accountStatusCache = accountStatusCache;
        _auditLogService = auditLogService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Updates supplied fields, revokes sessions on deactivation, and records actual changes atomically.</summary>
    public async Task<AdminUserCardDto> ExecuteAsync(
        int userId,
        UpdateModRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        _ = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Yêu cầu đăng nhập.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var mod = await _repository.GetModForUpdateAsync(userId, ct)
            ?? throw new NotFoundException("MOD_NOT_FOUND", "Không tìm thấy tài khoản Mod.");
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();

        if (request.DisplayName is not null)
        {
            var displayName = request.DisplayName.Trim();
            if (!string.Equals(mod.DisplayName, displayName, StringComparison.Ordinal))
            {
                oldValues["display_name"] = mod.DisplayName;
                newValues["display_name"] = displayName;
                mod.DisplayName = displayName;
            }
        }

        var statusChanged = false;
        var revokedSessions = false;
        if (request.Status is not null)
        {
            var status = request.Status == "active" ? UserStatus.Active : UserStatus.Deactivated;
            statusChanged = mod.Status != status;
            if (statusChanged)
            {
                oldValues["status"] = mod.Status;
                newValues["status"] = status;
                mod.Status = status;
                mod.SuspendedUntil = null;
            }

            if (status == UserStatus.Deactivated)
            {
                var sessions = await _repository.GetActiveSessionsAsync(userId, ct);
                foreach (var session in sessions)
                {
                    session.RevokedAt = now;
                }

                revokedSessions = sessions.Count > 0;
            }
        }

        if (oldValues.Count > 0 || revokedSessions)
        {
            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.ModUpdated,
                EntityType = AuditEntityTypes.User,
                EntityId = mod.Id,
                SubjectUserId = mod.Id,
                OldValues = oldValues.Count == 0 ? null : oldValues,
                NewValues = newValues.Count == 0 ? null : newValues
            });
            await _unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            if (statusChanged)
            {
                _accountStatusCache.Invalidate(userId);
            }
        }

        return AdministrationModMapper.ToCardDto(mod, mod.Role.Code);
    }
}
