using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Audit;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Encapsulates user sanction execution, status transitions, session revocation, audit and notification effects.</summary>
public sealed class UserSanctionEffects(IAuditLogService audit)
{
    /// <summary>Applies a sanction against the given user with full role guards and side effects.</summary>
    public async Task<UserSanction> ApplyAsync(
        IUserSanctionRepository repository,
        User user,
        SanctionType type,
        string reason,
        DateTime? endsAt,
        long? reportId,
        int actorId,
        DateTime now,
        CancellationToken ct)
    {
        if (user.Role.Code is "MOD" or "ADMIN" or "MODERATOR")
            throw new ForbiddenException("CANNOT_SANCTION_STAFF", "Không thể xử phạt tài khoản Mod hoặc Admin.");

        if (user.Status == UserStatus.Banned)
            throw new ConflictException("USER_ALREADY_BANNED", "Tài khoản đã bị cấm.");

        var oldUserStatus = user.Status;
        var oldSuspendedUntil = user.SuspendedUntil;

        var sanction = new UserSanction
        {
            UserId = user.Id,
            Type = type,
            Reason = reason,
            ReportId = reportId,
            IssuedBy = actorId,
            StartsAt = now,
            EndsAt = endsAt
        };
        repository.AddSanction(sanction);

        if (type != SanctionType.Warn)
        {
            user.Status = type == SanctionType.Ban ? UserStatus.Banned : UserStatus.Suspended;
            user.SuspendedUntil = endsAt;
            var sessions = await repository.GetSessionsAsync(user.Id, ct);
            foreach (var session in sessions)
            {
                session.RevokedAt = now;
            }
        }

        var userChanges = ChangedValues(reason,
            (nameof(user.Status), oldUserStatus, user.Status),
            (nameof(user.SuspendedUntil), oldSuspendedUntil, user.SuspendedUntil));

        audit.Add(new AuditLogEntry
        {
            Action = type switch
            {
                SanctionType.Warn => AuditActions.UserWarned,
                SanctionType.Suspend => AuditActions.UserSuspended,
                _ => AuditActions.UserBanned
            },
            EntityType = AuditEntityTypes.User,
            EntityId = user.Id,
            SubjectUserId = user.Id,
            OldValues = userChanges.Old,
            NewValues = userChanges.New
        });

        repository.AddNotification(new Domain.Entities.Notification
        {
            RecipientId = user.Id,
            Type = "ACCOUNT_SANCTIONED",
            Title = "Tài khoản đã nhận xử phạt",
            Body = reason,
            Data = JsonSerializer.Serialize(new { report_id = reportId })
        });

        return sanction;
    }

    private static (object? Old, object New) ChangedValues(string? reason,
        params (string Key, object? Old, object? New)[] columns)
    {
        var changed = columns.Where(column => !Equals(column.Old, column.New)).ToArray();
        var oldValues = changed.ToDictionary(column => JsonNamingPolicy.SnakeCaseLower.ConvertName(column.Key), column => column.Old);
        var newValues = changed.ToDictionary(column => JsonNamingPolicy.SnakeCaseLower.ConvertName(column.Key), column => column.New);
        newValues["reason"] = reason;
        return (oldValues.Count == 0 ? null : oldValues, newValues);
    }
}
