using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.UpdateBlogCommentStatus;

/// <summary>Implements MOD-B3: updates blog comment moderation status.</summary>
public sealed class UpdateBlogCommentStatusUseCase(
    IValidator<UpdateBlogCommentStatusRequestDto> validator,
    ModerationAccess access,
    IBlogModerationRepository repository,
    IUnitOfWork unitOfWork,
    IAuditLogService audit) : IUpdateBlogCommentStatusUseCase
{
    /// <summary>Updates comment status, adjusts blog comment count, logs audit, and commits in one transaction.</summary>
    public async Task<UpdateBlogCommentStatusResponseDto> ExecuteAsync(long commentId, UpdateBlogCommentStatusRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "BLOG_MODERATE");

        var targetStatus = ParseStatus(request.Status);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var comment = await repository.GetCommentByIdAsync(commentId, ct)
            ?? throw new NotFoundException("COMMENT_NOT_FOUND", "Không tìm thấy bình luận.");

        if (comment.Status != targetStatus)
        {
            var oldStatus = comment.Status;

            // Adjust denormalized comment count on the blog
            if (oldStatus == ContentStatus.Visible && targetStatus != ContentStatus.Visible)
            {
                await repository.AddToCommentCountAsync(comment.BlogId, -1, ct);
            }
            else if (oldStatus != ContentStatus.Visible && targetStatus == ContentStatus.Visible)
            {
                await repository.AddToCommentCountAsync(comment.BlogId, 1, ct);
            }

            comment.Status = targetStatus;

            var action = targetStatus switch
            {
                ContentStatus.Visible => AuditActions.CommentRestored,
                ContentStatus.Hidden => AuditActions.CommentHidden,
                _ => AuditActions.CommentRemoved
            };

            audit.Add(new AuditLogEntry
            {
                Action = action,
                EntityType = AuditEntityTypes.BlogComment,
                EntityId = comment.Id,
                SubjectUserId = comment.UserId,
                OldValues = new { Status = oldStatus },
                NewValues = new { comment.Status, Reason = request.Reason.Trim() }
            });
        }

        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new UpdateBlogCommentStatusResponseDto
        {
            CommentId = comment.Id,
            Status = ModerationMapper.Api(comment.Status)
        };
    }

    private static ContentStatus ParseStatus(string status) => status switch
    {
        "visible" => ContentStatus.Visible,
        "hidden" => ContentStatus.Hidden,
        "removed" => ContentStatus.Removed,
        _ => throw new BadRequestException("VALIDATION_FAILED", "Trạng thái bình luận không hợp lệ.")
    };
}
