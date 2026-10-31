using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.ResolveReport;

/// <summary>Implements MOD-RP4 by resolving reports and applying their action in one transaction.</summary>
public sealed class ResolveReportUseCase(IValidator<ResolveReportRequestDto> validator, ModerationAccess access,
    IRestaurantModerationRepository repository, IUnitOfWork unitOfWork, RestaurantModerationEffects effects,
    IAuditLogService audit, ICloudinaryService cloudinary, IAccountStatusCache accountStatusCache, TimeProvider clock) : IResolveReportUseCase
{
    /// <summary>Checks target-specific permissions, serializes the target workflow, and commits all report effects atomically.</summary>
    public async Task<ReportDto> ExecuteAsync(long reportId, ResolveReportRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "REPORT_RESTAURANT_HANDLE", "REPORT_SOCIAL_HANDLE");
        var target = await repository.GetReportTargetAsync(reportId, ct)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        var actorId = await access.RequireAsync(ct, target.Type == ReportTargetType.Restaurant
            ? "REPORT_RESTAURANT_HANDLE" : "REPORT_SOCIAL_HANDLE");
        if (request.ResolutionAction is "warn_user" or "suspend_user" or "ban_user")
            await access.RequireAsync(ct, request.ResolutionAction == "ban_user" ? "USER_BAN" : "USER_SUSPEND");
        var action = ParseAction(request.ResolutionAction);
        ValidateTargetAction(target.Type, action);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        Domain.Entities.Restaurant? restaurant = null;
        Blog? blog = null;
        int ownerId;
        if (target.Type == ReportTargetType.Restaurant)
        {
            restaurant = await repository.LockRestaurantAsync(target.Id, ct)
                ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
            ownerId = restaurant.OwnerId;
        }
        else
        {
            blog = await repository.LockBlogAsync(target.Id, ct)
                ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");
            ownerId = blog.AuthorId;
        }

        var reports = await repository.LockReportsAsync(target, reportId, ct);
        var report = reports.FirstOrDefault(item => item.Id == reportId)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        if (!IsOpen(report)) throw new ConflictException("REPORT_CLOSED", "Báo cáo đã được xử lý.");
        if (report.HandledBy.HasValue && report.HandledBy.Value != actorId)
            throw new ForbiddenException("FORBIDDEN", "Chỉ người đã nhận xử lý mới được giải quyết báo cáo này.");
        var now = clock.GetUtcNow().UtcDateTime;
        var note = string.IsNullOrWhiteSpace(request.ResolutionNote) ? null : request.ResolutionNote.Trim();
        var invalidatedUserId = await ApplyActionAsync(action, restaurant, blog, ownerId, actorId, reportId, request, note, now, ct);
        var affected = request.ApplyToRelatedReports ? reports.Where(IsOpen).ToArray() : [report];
        foreach (var item in affected)
        {
            var oldStatus = item.Status;
            var oldHandledBy = item.HandledBy;
            var oldHandledAt = item.HandledAt;
            var oldAction = item.ResolutionAction;
            var oldNote = item.ResolutionNote;
            item.Status = request.Outcome == "resolved" ? ReportStatus.Resolved : ReportStatus.Dismissed;
            item.HandledBy = actorId;
            item.HandledAt = now;
            item.ResolutionAction = action;
            item.ResolutionNote = note;
            var changes = ChangedValues(note,
                (nameof(item.Status), oldStatus, item.Status),
                (nameof(item.HandledBy), oldHandledBy, item.HandledBy),
                (nameof(item.HandledAt), oldHandledAt, item.HandledAt),
                (nameof(item.ResolutionAction), oldAction, item.ResolutionAction),
                (nameof(item.ResolutionNote), oldNote, item.ResolutionNote));
            audit.Add(new AuditLogEntry
            {
                Action = AuditActions.ReportResolved, EntityType = AuditEntityTypes.Report, EntityId = item.Id,
                SubjectUserId = ownerId,
                OldValues = changes.Old, NewValues = changes.New
            });
            Notify(item.ReporterId, item.Id, target.Id, target.Type, note);
        }
        // The content owner receives the conclusion even when the chosen action is none.
        if (!affected.Any(item => item.ReporterId == ownerId)) Notify(ownerId, report.Id, target.Id, target.Type, note);
        await unitOfWork.SaveChangesAsync(ct);
        var display = await repository.GetReportDisplayDataAsync(target, [report], ct);
        var response = ModerationMapper.Report(report, display, cloudinary);
        await transaction.CommitAsync(ct);
        if (invalidatedUserId.HasValue) accountStatusCache.Invalidate(invalidatedUserId.Value);
        return response;
    }

    /// <summary>Applies a restaurant, blog or account action and returns the account cache key to invalidate after commit.</summary>
    private async Task<int?> ApplyActionAsync(ResolutionAction action, Domain.Entities.Restaurant? restaurant, Blog? blog,
        int ownerId, int actorId, long reportId, ResolveReportRequestDto request, string? note, DateTime now, CancellationToken ct)
    {
        if (action is ResolutionAction.HideRestaurant or ResolutionAction.SuspendRestaurant)
            effects.Apply(restaurant!, action == ResolutionAction.HideRestaurant ? RestaurantModerationStatus.Hidden : RestaurantModerationStatus.Suspended, note, reportId);
        if (action is ResolutionAction.HideBlog or ResolutionAction.RemoveBlog)
        {
            var oldStatus = blog!.Status;
            blog.Status = action == ResolutionAction.HideBlog ? BlogStatus.Hidden : BlogStatus.Removed;
            if (oldStatus != blog.Status) audit.Add(new AuditLogEntry
            {
                Action = action == ResolutionAction.HideBlog ? AuditActions.BlogHidden : AuditActions.BlogRemoved,
                EntityType = AuditEntityTypes.Blog, EntityId = blog.Id, SubjectUserId = ownerId,
                OldValues = new { Status = oldStatus }, NewValues = new { blog.Status, Reason = note }
            });
        }
        if (action is not (ResolutionAction.WarnUser or ResolutionAction.SuspendUser or ResolutionAction.BanUser)) return null;

        var user = await repository.LockUserAsync(ownerId, ct)
            ?? throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy tài khoản.");
        if (user.Role.Code is "MOD" or "ADMIN")
            throw new ForbiddenException("CANNOT_SANCTION_STAFF", "Không thể xử phạt tài khoản Mod hoặc Admin.");
        if (user.Status == UserStatus.Banned)
            throw new ConflictException("USER_ALREADY_BANNED", "Tài khoản đã bị cấm.");
        var oldUserStatus = user.Status;
        var oldSuspendedUntil = user.SuspendedUntil;
        var sanctionType = action switch
        {
            ResolutionAction.WarnUser => SanctionType.Warn,
            ResolutionAction.SuspendUser => SanctionType.Suspend,
            _ => SanctionType.Ban
        };
        var endsAt = sanctionType == SanctionType.Suspend ? request.SuspendUntil!.Value.UtcDateTime : (DateTime?)null;
        repository.AddSanction(new UserSanction
        {
            UserId = ownerId, IssuedBy = actorId, ReportId = reportId, Type = sanctionType,
            Reason = note ?? "Xử lý vi phạm theo báo cáo.", StartsAt = now, EndsAt = endsAt
        });
        if (sanctionType != SanctionType.Warn)
        {
            user.Status = sanctionType == SanctionType.Ban ? UserStatus.Banned : UserStatus.Suspended;
            user.SuspendedUntil = endsAt;
            foreach (var session in await repository.GetSessionsAsync(ownerId, ct)) session.RevokedAt = now;
        }
        var userChanges = ChangedValues(note, (nameof(user.Status), oldUserStatus, user.Status),
            (nameof(user.SuspendedUntil), oldSuspendedUntil, user.SuspendedUntil));
        audit.Add(new AuditLogEntry
        {
            Action = sanctionType switch { SanctionType.Warn => AuditActions.UserWarned, SanctionType.Suspend => AuditActions.UserSuspended, _ => AuditActions.UserBanned },
            EntityType = AuditEntityTypes.User, EntityId = ownerId, SubjectUserId = ownerId,
            OldValues = userChanges.Old, NewValues = userChanges.New
        });
        repository.AddNotification(new Domain.Entities.Notification
        {
            RecipientId = ownerId, Type = "ACCOUNT_SANCTIONED", Title = "Tài khoản đã nhận xử phạt",
            Body = note, Data = JsonSerializer.Serialize(new { report_id = reportId })
        });
        return sanctionType == SanctionType.Warn ? null : ownerId;
    }

    /// <summary>Stages a report conclusion notification with routing identifiers.</summary>
    private void Notify(int recipientId, long reportId, long targetId, ReportTargetType targetType, string? note) =>
        repository.AddNotification(new Domain.Entities.Notification
        {
            RecipientId = recipientId, Type = "REPORT_RESOLVED", Title = "Báo cáo đã được xử lý", Body = note,
            Data = JsonSerializer.Serialize(new { report_id = reportId, target_id = targetId, target_type = ModerationMapper.Api(targetType) })
        });

    /// <summary>Recognizes the two report states that can still receive a conclusion.</summary>
    private static bool IsOpen(Report report) => report.Status is ReportStatus.Pending or ReportStatus.InReview;

    /// <summary>Rejects actions that affect a different kind of content than the report target.</summary>
    private static void ValidateTargetAction(ReportTargetType type, ResolutionAction action)
    {
        if ((type == ReportTargetType.Restaurant && action is ResolutionAction.HideBlog or ResolutionAction.RemoveBlog) ||
            (type == ReportTargetType.Blog && action is ResolutionAction.HideRestaurant or ResolutionAction.SuspendRestaurant))
            throw new BadRequestException("ACTION_NOT_ALLOWED_FOR_TARGET", "Hành động không phù hợp với đối tượng báo cáo.");
    }

    /// <summary>Parses a previously validated exact API enum string.</summary>
    private static ResolutionAction ParseAction(string value) => Enum.GetValues<ResolutionAction>()
        .Single(action => ModerationMapper.Api(action) == value);

    /// <summary>Includes only changed columns in audit JSON, using snake_case dictionary keys and the moderator's reason.</summary>
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
