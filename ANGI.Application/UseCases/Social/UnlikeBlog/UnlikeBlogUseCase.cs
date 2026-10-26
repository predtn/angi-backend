using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Social.UnlikeBlog;

/// <summary>Implements BLOG-09 to unlike a blog idempotently.</summary>
public sealed class UnlikeBlogUseCase : IUnlikeBlogUseCase
{
    private readonly IBlogRepository _blogRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    public UnlikeBlogUseCase(
        IBlogRepository blogRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _blogRepository = blogRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    public async Task<BlogLikeResponseDto> ExecuteAsync(long blogId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thao tác.");

        var blog = await _blogRepository.GetByIdAsync(blogId, ct)
            ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");

        if (blog.Status != BlogStatus.Published)
        {
            throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var removed = await _blogRepository.TryRemoveLikeAsync(blogId, userId, ct);
        var likeCount = await _blogRepository.AddToLikeCountAsync(blogId, removed ? -1 : 0, ct);
        await transaction.CommitAsync(ct);

        return new BlogLikeResponseDto
        {
            Liked = false,
            LikeCount = likeCount
        };
    }
}
