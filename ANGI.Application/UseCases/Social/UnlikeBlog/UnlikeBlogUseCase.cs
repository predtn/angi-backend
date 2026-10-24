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

        var existingLike = await _blogRepository.GetLikeAsync(blogId, userId, ct);
        if (existingLike is not null)
        {
            _blogRepository.RemoveLike(existingLike);
            blog.LikeCount = Math.Max(0, blog.LikeCount - 1);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return new BlogLikeResponseDto
        {
            Liked = false,
            LikeCount = blog.LikeCount
        };
    }
}
