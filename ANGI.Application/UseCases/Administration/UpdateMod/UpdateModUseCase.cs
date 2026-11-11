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
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Require _currentUserService.UserId.
        _ = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Yêu cầu đăng nhập.");
        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Prepare mod from repository.GetModForUpdateAsync, now from timeProvider.GetUtcNow, oldValues as Dictionary<string, object?> and newValues as Dictionary<string, object?>.
        var mod = await _repository.GetModForUpdateAsync(userId, ct)
            ?? throw new NotFoundException("MOD_NOT_FOUND", "Không tìm thấy tài khoản Mod.");
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();

        // STEP-05: When request.DisplayName exists, prepare displayName through request.DisplayName.Trim and check whether mod.DisplayName differs from displayName.
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

        // STEP-06: Prepare statusChanged and revokedSessions.
        var statusChanged = false;
        var revokedSessions = false;
        // STEP-07: When request.Status exists, run the branch below.
        if (request.Status is not null)
        {
            // STEP-08: Prepare status.
            var status = request.Status == "active" ? UserStatus.Active : UserStatus.Deactivated;
            // STEP-09: Set statusChanged.
            statusChanged = mod.Status != status;
            // STEP-10: When statusChanged is true, run the branch below.
            if (statusChanged)
            {
                // STEP-11: Set oldValues["status"], newValues["status"], mod.Status and mod.SuspendedUntil.
                oldValues["status"] = mod.Status;
                newValues["status"] = status;
                mod.Status = status;
                mod.SuspendedUntil = null;
            }

            // STEP-12: When status is UserStatus.Deactivated, prepare sessions through repository.GetActiveSessionsAsync, go through each session in sessions and 1 more action(s).
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

        // STEP-13: When oldValues.Count > 0 or revokedSessions is true, run the branch below.
        if (oldValues.Count > 0 || revokedSessions)
        {
            // STEP-14: Record audit action AuditActions.ModUpdated.
            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.ModUpdated,
                EntityType = AuditEntityTypes.User,
                EntityId = mod.Id,
                SubjectUserId = mod.Id,
                OldValues = oldValues.Count == 0 ? null : oldValues,
                NewValues = newValues.Count == 0 ? null : newValues
            });
            // STEP-15: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);
            // STEP-16: Commit the transaction.
            await transaction.CommitAsync(ct);

            // STEP-17: When statusChanged is true, call accountStatusCache.Invalidate.
            if (statusChanged)
            {
                _accountStatusCache.Invalidate(userId);
            }
        }

        // STEP-18: Return the result of AdministrationModMapper.ToCardDto.
        return AdministrationModMapper.ToCardDto(mod, mod.Role.Code);
    }
}
