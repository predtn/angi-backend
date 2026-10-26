using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Social.LikeBlog;

/// <summary>Implements BLOG-08 to like a blog idempotently.</summary>
public sealed class LikeBlogUseCase : ILikeBlogUseCase
{
    private readonly IBlogRepository _blogRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public LikeBlogUseCase(
        IBlogRepository blogRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _blogRepository = blogRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<BlogLikeResponseDto> ExecuteAsync(long blogId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thích bài viết.");

        var blog = await _blogRepository.GetByIdAsync(blogId, ct)
            ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");

        if (blog.Status != BlogStatus.Published)
        {
            throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var inserted = await _blogRepository.TryAddLikeAsync(blogId, userId, _timeProvider.GetUtcNow().UtcDateTime, ct);

        // A repeated like changes nothing (BLOG-08); delta 0 only reads the current count.
        var likeCount = await _blogRepository.AddToLikeCountAsync(blogId, inserted ? 1 : 0, ct);
        await transaction.CommitAsync(ct);

        return new BlogLikeResponseDto
        {
            Liked = true,
            LikeCount = likeCount
        };
    }
}
