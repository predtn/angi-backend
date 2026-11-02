using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Audit;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Shares blog state changes, audit records, and author notifications across MOD-B2 and MOD-RP4.</summary>
public sealed class BlogModerationEffects(IBlogModerationRepository repository, IAuditLogService audit)
{
    /// <summary>Changes blog status and stages the matching audit and author notification once.</summary>
    public void Apply(Blog blog, BlogStatus status, string? reason, long? reportId = null)
    {
        if (blog.Status == status) return;
        var oldStatus = blog.Status;
        blog.Status = status;
        var action = status switch
        {
            BlogStatus.Published => AuditActions.BlogRestored,
            BlogStatus.Hidden => AuditActions.BlogHidden,
            _ => AuditActions.BlogRemoved
        };

        audit.Add(new AuditLogEntry
        {
            Action = action,
            EntityType = AuditEntityTypes.Blog,
            EntityId = blog.Id,
            SubjectUserId = blog.AuthorId,
            OldValues = new { Status = oldStatus },
            NewValues = new { blog.Status, Reason = reason }
        });

        repository.AddNotification(new Domain.Entities.Notification
        {
            RecipientId = blog.AuthorId,
            Type = action,
            Title = "Trạng thái bài viết đã thay đổi",
            Body = reason,
            Data = JsonSerializer.Serialize(new
            {
                blog_id = blog.Id,
                report_id = reportId,
                status = ModerationMapper.Api(status)
            })
        });
    }
}
