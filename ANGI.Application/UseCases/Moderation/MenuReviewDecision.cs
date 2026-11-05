using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Audit;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Shares pending-state validation and transactional review side effects.</summary>
internal static class MenuReviewDecision
{
    /// <summary>Rejects every draft, cancelled or already reviewed submission with the documented conflict.</summary>
    public static void RequirePending(MenuSubmission submission)
    {
        if (submission.Status != MenuSubmissionStatus.Pending)
            throw new ConflictException("SUBMISSION_NOT_PENDING", "Bản menu không còn chờ duyệt.");
    }

    /// <summary>Records review metadata, the owner's notification and the matching audit action before saving.</summary>
    public static void Record(MenuSubmission submission, int reviewerId, string? note, bool approved,
        TimeProvider timeProvider, IMenuReviewRepository repository, IAuditLogService auditLog)
    {
        // STEP-01: Prepare oldStatus.
        var oldStatus = submission.Status;
        // STEP-02: Update submission: Status, ReviewedBy, ReviewedAt and ReviewNote.
        submission.Status = approved ? MenuSubmissionStatus.Approved : MenuSubmissionStatus.Rejected;
        submission.ReviewedBy = reviewerId;
        submission.ReviewedAt = timeProvider.GetUtcNow().UtcDateTime;
        submission.ReviewNote = note;
        // STEP-03: Queue a new ANGI.Domain.Entities.Notification through repository.AddNotification.
        repository.AddNotification(new ANGI.Domain.Entities.Notification
        {
            RecipientId = submission.Restaurant.OwnerId,
            Type = approved ? "MENU_APPROVED" : "MENU_REJECTED",
            Title = approved ? "Menu đã được duyệt" : "Menu bị từ chối",
            Body = note,
            Data = JsonSerializer.Serialize(new { submission_id = submission.Id, restaurant_id = submission.RestaurantId })
        });
        // STEP-04: Record audit action AuditActions.MenuApproved and AuditActions.MenuRejected.
        auditLog.Add(new AuditLogEntry
        {
            Action = approved ? AuditActions.MenuApproved : AuditActions.MenuRejected,
            EntityType = AuditEntityTypes.MenuSubmission,
            EntityId = submission.Id,
            SubjectUserId = submission.Restaurant.OwnerId,
            OldValues = new { Status = oldStatus },
            NewValues = new { submission.Status, submission.ReviewedBy, submission.ReviewedAt, submission.ReviewNote, Reason = note }
        });
    }
}
