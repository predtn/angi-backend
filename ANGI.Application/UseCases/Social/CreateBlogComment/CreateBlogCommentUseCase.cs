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

/// <summary>Implements BLOG-10 to add a comment to a published blog.</summary>
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

        _blogRepository.AddComment(comment);
        blog.CommentCount += 1;
        await _unitOfWork.SaveChangesAsync(ct);

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
}
