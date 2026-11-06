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
    IAuditLogService audit, ICloudinaryService cloudinary, IAccountStatusCache accountStatusCache, TimeProvider clock,
    UserSanctionEffects? sanctionEffects = null, BlogModerationEffects? blogEffects = null) : IResolveReportUseCase
{
    private readonly UserSanctionEffects _sanctionEffects = sanctionEffects ?? new UserSanctionEffects(audit);
    /// <summary>Checks target-specific permissions, serializes the target workflow, and commits all report effects atomically.</summary>
    public async Task<ReportDto> ExecuteAsync(long reportId, ResolveReportRequestDto request, CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await validator.ValidateAndThrowAsync(request, ct);
        // STEP-02: Check the caller's permission through access.RequireAsync.
        await access.RequireAsync(ct, "REPORT_RESTAURANT_HANDLE", "REPORT_SOCIAL_HANDLE");
        // STEP-03: Prepare target from repository.GetReportTargetAsync.
        var target = await repository.GetReportTargetAsync(reportId, ct)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        // STEP-04: Check the caller's permission through access.RequireAsync.
        var actorId = await access.RequireAsync(ct, target.Type == ReportTargetType.Restaurant
            ? "REPORT_RESTAURANT_HANDLE" : "REPORT_SOCIAL_HANDLE");
        // STEP-05: When request.ResolutionAction is one of the listed values, call access.RequireAsync.
        if (request.ResolutionAction is "warn_user" or "suspend_user" or "ban_user")
            await access.RequireAsync(ct, request.ResolutionAction == "ban_user" ? "USER_BAN" : "USER_SUSPEND");
        // STEP-06: Prepare action through ParseAction.
        var action = ParseAction(request.ResolutionAction);
        // STEP-07: Run ValidateTargetAction.
        ValidateTargetAction(target.Type, action);

        // STEP-08: Open the database transaction.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        // STEP-09: Prepare restaurant, blog and ownerId.
        Domain.Entities.Restaurant? restaurant = null;
        Blog? blog = null;
        int ownerId;
        // STEP-10: When target.Type is ReportTargetType.Restaurant, set restaurant and set ownerId; otherwise set blog and set ownerId.
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

        // STEP-11: Load reports through repository.LockReportsAsync.
        var reports = await repository.LockReportsAsync(target, reportId, ct);
        // STEP-12: Prepare report from reports.FirstOrDefault.
        var report = reports.FirstOrDefault(item => item.Id == reportId)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        // STEP-13: Reject with REPORT_CLOSED when IsOpen(report) does not hold.
        if (!IsOpen(report)) throw new ConflictException("REPORT_CLOSED", "Báo cáo đã được xử lý.");
        // STEP-14: Prepare now and note, then call ApplyActionAsync and keep invalidatedUserId.
        var now = clock.GetUtcNow().UtcDateTime;
        var note = string.IsNullOrWhiteSpace(request.ResolutionNote) ? null : request.ResolutionNote.Trim();
        var invalidatedUserId = await ApplyActionAsync(action, restaurant, blog, ownerId, actorId, reportId, request, note, now, ct);
        // STEP-15: Prepare affected from reports.Where.
        var affected = request.ApplyToRelatedReports ? reports.Where(IsOpen).ToArray() : [report];
        // STEP-16: Go through each item in affected.
        foreach (var item in affected)
        {
            // STEP-17: Prepare oldStatus, oldHandledBy, oldHandledAt, oldAction and 1 more.
            var oldStatus = item.Status;
            var oldHandledBy = item.HandledBy;
            var oldHandledAt = item.HandledAt;
            var oldAction = item.ResolutionAction;
            var oldNote = item.ResolutionNote;
            // STEP-18: Update item: Status, HandledBy, HandledAt, ResolutionAction and 1 more.
            item.Status = request.Outcome == "resolved" ? ReportStatus.Resolved : ReportStatus.Dismissed;
            item.HandledBy = actorId;
            item.HandledAt = now;
            item.ResolutionAction = action;
            item.ResolutionNote = note;
            // STEP-19: Prepare changes through ChangedValues.
            var changes = ChangedValues(note,
                (nameof(item.Status), oldStatus, item.Status),
                (nameof(item.HandledBy), oldHandledBy, item.HandledBy),
                (nameof(item.HandledAt), oldHandledAt, item.HandledAt),
                (nameof(item.ResolutionAction), oldAction, item.ResolutionAction),
                (nameof(item.ResolutionNote), oldNote, item.ResolutionNote));
            // STEP-20: Record audit action AuditActions.ReportResolved.
            audit.Add(new AuditLogEntry
            {
                Action = AuditActions.ReportResolved, EntityType = AuditEntityTypes.Report, EntityId = item.Id,
                SubjectUserId = ownerId,
                OldValues = changes.Old, NewValues = changes.New
            });
            // STEP-21: Call Notify.
            Notify(item.ReporterId, item.Id, target.Id, target.Type, note);
        }
        // STEP-22: When affected.Any does not match the filter, call Notify.
        // The content owner receives the conclusion even when the chosen action is none.
        if (!affected.Any(item => item.ReporterId == ownerId)) Notify(ownerId, report.Id, target.Id, target.Type, note);
        // STEP-23: Save the pending changes.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-24: Load display through repository.GetReportDisplayDataAsync.
        var display = await repository.GetReportDisplayDataAsync(target, [report], ct);
        // STEP-25: Prepare response through ModerationMapper.Report.
        var response = ModerationMapper.Report(report, display, cloudinary);
        // STEP-26: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-27: When invalidatedUserId has a value, call accountStatusCache.Invalidate.
        if (invalidatedUserId.HasValue) accountStatusCache.Invalidate(invalidatedUserId.Value);
        // STEP-28: Return response.
        return response;
    }

    /// <summary>Applies a restaurant, blog or account action and returns the account cache key to invalidate after commit.</summary>
    private async Task<int?> ApplyActionAsync(ResolutionAction action, Domain.Entities.Restaurant? restaurant, Blog? blog,
        int ownerId, int actorId, long reportId, ResolveReportRequestDto request, string? note, DateTime now, CancellationToken ct)
    {
        // STEP-01: When action is one of the listed values, call effects.Apply.
        if (action is ResolutionAction.HideRestaurant or ResolutionAction.SuspendRestaurant)
            effects.Apply(restaurant!, action == ResolutionAction.HideRestaurant ? RestaurantModerationStatus.Hidden : RestaurantModerationStatus.Suspended, note, reportId);
        // STEP-02: When action is one of the listed values, prepare targetStatus and check whether blogEffects exists.
        if (action is ResolutionAction.HideBlog or ResolutionAction.RemoveBlog)
        {
            var targetStatus = action == ResolutionAction.HideBlog ? BlogStatus.Hidden : BlogStatus.Removed;
            if (blogEffects != null)
            {
                blogEffects.Apply(blog!, targetStatus, note, reportId);
            }
            else
            {
                var oldStatus = blog!.Status;
                blog.Status = targetStatus;
                if (oldStatus != blog.Status) audit.Add(new AuditLogEntry
                {
                    Action = action == ResolutionAction.HideBlog ? AuditActions.BlogHidden : AuditActions.BlogRemoved,
                    EntityType = AuditEntityTypes.Blog, EntityId = blog.Id, SubjectUserId = ownerId,
                    OldValues = new { Status = oldStatus }, NewValues = new { blog.Status, Reason = note }
                });
            }
        }
        // STEP-03: Return null early when action is the computed value.
        if (action is not (ResolutionAction.WarnUser or ResolutionAction.SuspendUser or ResolutionAction.BanUser)) return null;

        // STEP-04: Prepare user, sanctionType and endsAt, then call sanctionEffects.ApplyAsync.
        var user = await repository.LockUserAsync(ownerId, ct)
            ?? throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy tài khoản.");

        var sanctionType = action switch
        {
            ResolutionAction.WarnUser => SanctionType.Warn,
            ResolutionAction.SuspendUser => SanctionType.Suspend,
            _ => SanctionType.Ban
        };
        var endsAt = sanctionType == SanctionType.Suspend ? request.SuspendUntil!.Value.UtcDateTime : (DateTime?)null;

        await _sanctionEffects.ApplyAsync(repository, user, sanctionType, note ?? "Xử lý vi phạm theo báo cáo.", endsAt, reportId, actorId, now, ct);

        // STEP-05: Return one of two results depending on sanctionType is SanctionType.Warn.
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
        // STEP-01: Prepare changed through columns.Where(filtered).ToArray, oldValues through changed.ToDictionary and newValues through changed.ToDictionary.
        var changed = columns.Where(column => !Equals(column.Old, column.New)).ToArray();
        var oldValues = changed.ToDictionary(column => JsonNamingPolicy.SnakeCaseLower.ConvertName(column.Key), column => column.Old);
        var newValues = changed.ToDictionary(column => JsonNamingPolicy.SnakeCaseLower.ConvertName(column.Key), column => column.New);
        // STEP-02: Set newValues["reason"].
        newValues["reason"] = reason;
        // STEP-03: Return the collected values.
        return (oldValues.Count == 0 ? null : oldValues, newValues);
    }
}
