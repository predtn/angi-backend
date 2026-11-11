using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Social;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Social.CreateBlogComment;

/// <summary>Implements BLOG-10: adds a comment to a published blog and notifies its author (BLOG_COMMENTED).</summary>
public sealed class CreateBlogCommentUseCase : ICreateBlogCommentUseCase
{
    private readonly IValidator<CreateBlogCommentRequestDto> _validator;
    private readonly IBlogRepository _blogRepository;
    private readonly IBlogContentSanitizer _contentSanitizer;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    public CreateBlogCommentUseCase(
        IValidator<CreateBlogCommentRequestDto> validator,
        IBlogRepository blogRepository,
        IBlogContentSanitizer contentSanitizer,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _blogRepository = blogRepository;
        _contentSanitizer = contentSanitizer;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    public async Task<CommentDto> ExecuteAsync(
        long blogId,
        CreateBlogCommentRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để bình luận.");

        var user = await _blogRepository.GetAuthorAsync(userId, ct)
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Tài khoản không còn tồn tại.");

        var blog = await _blogRepository.GetByIdAsync(blogId, ct)
            ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");

        if (blog.Status != BlogStatus.Published)
        {
            throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");
        }

        var sanitizedContent = _contentSanitizer.Sanitize(request.Content.Trim());
        if (string.IsNullOrWhiteSpace(sanitizedContent))
        {
            throw new BadRequestException("VALIDATION_FAILED", "Nội dung bình luận không hợp lệ sau khi xử lý.");
        }

        var comment = new BlogComment
        {
            BlogId = blogId,
            UserId = userId,
            Content = sanitizedContent,
            Status = ContentStatus.Visible
        };

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        _blogRepository.AddComment(comment);
        if (blog.AuthorId != userId)
        {
            _blogRepository.AddNotification(CreateAuthorNotification(blog, user, sanitizedContent));
        }

        await _unitOfWork.SaveChangesAsync(ct);
        await _blogRepository.AddToCommentCountAsync(blogId, 1, ct);
        await transaction.CommitAsync(ct);

        return new CommentDto
        {
            Id = comment.Id,
            BlogId = blogId,
            User = new UserSummaryDto
            {
                Id = user.Id,
                DisplayName = user.DisplayName,
                AvatarUrl = user.AvatarMedia?.Url
            },
            Content = comment.Content,
            CreatedAt = comment.CreatedAt,
            IsMine = true
        };
    }

    /// <summary>Builds the BLOG_COMMENTED notification for the blog author with a short comment excerpt.</summary>
    private static Domain.Entities.Notification CreateAuthorNotification(Blog blog, User commenter, string content)
    {
        var excerpt = content.Length > 50 ? string.Concat(content.AsSpan(0, 47), "...") : content;
        return new Domain.Entities.Notification
        {
            RecipientId = blog.AuthorId,
            Type = "BLOG_COMMENTED",
            Title = "Có bình luận mới",
            Body = $"{commenter.DisplayName}: \"{excerpt}\"",
            Data = JsonSerializer.Serialize(new { blog_id = blog.Id })
        };
    }
}
