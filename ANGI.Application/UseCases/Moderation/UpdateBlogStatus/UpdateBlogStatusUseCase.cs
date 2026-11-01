using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.UpdateBlogStatus;

/// <summary>Implements MOD-B2: updates blog moderation status.</summary>
public sealed class UpdateBlogStatusUseCase(
    IValidator<UpdateBlogStatusRequestDto> validator,
    ModerationAccess access,
    IBlogModerationRepository repository,
    IUnitOfWork unitOfWork,
    BlogModerationEffects effects) : IUpdateBlogStatusUseCase
{
    /// <summary>Updates blog status, applies audit/notifications, and commits in one transaction.</summary>
    public async Task<UpdateBlogStatusResponseDto> ExecuteAsync(long blogId, UpdateBlogStatusRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "BLOG_MODERATE");

        var targetStatus = ParseStatus(request.Status);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var blog = await repository.GetBlogByIdAsync(blogId, ct)
            ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");

        effects.Apply(blog, targetStatus, request.Reason.Trim());

        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new UpdateBlogStatusResponseDto
        {
            BlogId = blog.Id,
            Status = ModerationMapper.Api(blog.Status)
        };
    }

    private static BlogStatus ParseStatus(string status) => status switch
    {
        "published" => BlogStatus.Published,
        "hidden" => BlogStatus.Hidden,
        "removed" => BlogStatus.Removed,
        _ => throw new BadRequestException("VALIDATION_FAILED", "Trạng thái bài viết không hợp lệ.")
    };
}
