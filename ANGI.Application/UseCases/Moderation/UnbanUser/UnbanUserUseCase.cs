using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.UnbanUser;

/// <summary>Implements MOD-U4 by lifting an active ban or suspension and recording audit and notification records.</summary>
public sealed class UnbanUserUseCase(
    IValidator<UnbanUserRequestDto> validator,
    ModerationAccess access,
    IUserModerationRepository repository,
    IUnitOfWork unitOfWork,
    IAuditLogService audit,
    IAccountStatusCache accountStatusCache,
    TimeProvider clock) : IUnbanUserUseCase
{
    /// <summary>Validates request, checks USER_BAN permission, restores user status to active, and updates sanctions atomically.</summary>
    public async Task<AdminUserCardDto> ExecuteAsync(int userId, UnbanUserRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var actorId = await access.RequireAsync(ct, "USER_BAN");

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var user = await repository.LockUserAsync(userId, ct)
            ?? throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy người dùng.");

        if (user.Role.Code is "MOD" or "ADMIN")
            throw new ForbiddenException("CANNOT_SANCTION_STAFF", "Không thể xử phạt hoặc thao tác trên tài khoản Mod hoặc Admin.");

        if (user.Status != UserStatus.Banned && user.Status != UserStatus.Suspended)
            throw new ConflictException("USER_NOT_SANCTIONED", "Người dùng không có lệnh cấm hoặc tạm khóa nào đang hiệu lực.");

        var now = clock.GetUtcNow().UtcDateTime;
        var activeSanctions = await repository.GetActiveSanctionsAsync(userId, now, ct);
        if (activeSanctions.Count == 0)
            throw new ConflictException("USER_NOT_SANCTIONED", "Người dùng không có lệnh cấm hoặc tạm khóa nào đang hiệu lực.");

        var oldStatus = user.Status;
        var oldSuspendedUntil = user.SuspendedUntil;

        user.Status = UserStatus.Active;
        user.SuspendedUntil = null;

        foreach (var sanction in activeSanctions)
        {
            sanction.LiftedBy = actorId;
            sanction.LiftedAt = now;
            sanction.LiftReason = request.LiftReason.Trim();
        }

        repository.AddNotification(new Domain.Entities.Notification
        {
            RecipientId = user.Id,
            Type = "ACCOUNT_UNBANNED",
            Title = "Tài khoản đã được gỡ phạt",
            Body = request.LiftReason.Trim(),
            Data = JsonSerializer.Serialize(new { user_id = user.Id })
        });

        audit.Add(new AuditLogEntry
        {
            Action = AuditActions.UserUnbanned,
            EntityType = AuditEntityTypes.User,
            EntityId = user.Id,
            SubjectUserId = user.Id,
            OldValues = new { Status = oldStatus, SuspendedUntil = oldSuspendedUntil },
            NewValues = new { Status = UserStatus.Active, SuspendedUntil = (DateTime?)null, Reason = request.LiftReason.Trim() }
        });

        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        accountStatusCache.Invalidate(user.Id);

        return ModerationMapper.AdminUser(user);
    }
}
