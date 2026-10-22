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
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Social.CreateBlogComment;

/// <summary>Implements BLOG-10: creates a top-level comment on an active blog.</summary>
public sealed class CreateBlogCommentUseCase : ICreateBlogCommentUseCase
{
    private readonly IValidator<CreateBlogCommentRequestDto> _validator;
    private readonly IBlogCommentRepository _blogCommentRepository;
    private readonly IBlogContentSanitizer _contentSanitizer;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes dependencies for BLOG-10 use case.</summary>
    public CreateBlogCommentUseCase(
        IValidator<CreateBlogCommentRequestDto> validator,
        IBlogCommentRepository blogCommentRepository,
        IBlogContentSanitizer contentSanitizer,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _blogCommentRepository = blogCommentRepository;
        _contentSanitizer = contentSanitizer;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<CommentDto> ExecuteAsync(long blogId, CreateBlogCommentRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var authorId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để bình luận.");

        var blog = await _blogCommentRepository.GetActiveBlogByIdAsync(blogId, ct)
            ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài blog.");

        var author = await _blogCommentRepository.GetUserAsync(authorId, ct)
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Tài khoản không còn tồn tại.");

        var sanitizedContent = _contentSanitizer.Sanitize(request.Content.Trim());
        if (string.IsNullOrWhiteSpace(sanitizedContent))
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(request.Content), "Nội dung bình luận không hợp lệ.")
            ]);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var comment = new BlogComment
        {
            BlogId = blogId,
            UserId = authorId,
            Content = sanitizedContent,
            Status = ContentStatus.Visible,
            CreatedAt = now,
            UpdatedAt = now
        };

        _blogCommentRepository.Add(comment);
        blog.CommentCount++;

        if (blog.AuthorId != authorId)
        {
            var excerpt = sanitizedContent.Length > 50
                ? string.Concat(sanitizedContent.AsSpan(0, 47), "...")
                : sanitizedContent;

            _blogCommentRepository.AddNotification(new Domain.Entities.Notification
            {
                RecipientId = blog.AuthorId,
                Type = "BLOG_COMMENTED",
                Title = "Có bình luận mới",
                Body = $"{author.DisplayName}: \"{excerpt}\"",
                Data = JsonSerializer.Serialize(new { blogId = blog.Id }),
                CreatedAt = now
            });
        }

        await _unitOfWork.SaveChangesAsync(ct);

        return new CommentDto
        {
            Id = comment.Id,
            BlogId = blogId,
            User = new UserSummaryDto
            {
                Id = author.Id,
                DisplayName = author.DisplayName,
                AvatarUrl = author.AvatarMedia?.Url
            },
            Content = comment.Content,
            CreatedAt = comment.CreatedAt,
            IsMine = true
        };
    }
}
