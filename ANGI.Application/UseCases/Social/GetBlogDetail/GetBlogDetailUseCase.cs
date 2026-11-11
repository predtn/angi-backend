using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;

namespace ANGI.Application.UseCases.Social.GetBlogDetail;

/// <summary>Implements BLOG-02 by retrieving the full detail of a published blog, author, images, and roadmap snapshot.</summary>
public sealed class GetBlogDetailUseCase : IGetBlogDetailUseCase
{
    private readonly IBlogRepository _blogRepository;
    private readonly ICurrentUserService _currentUserService;

    /// <summary>Initializes BLOG-02 with blog persistence and caller identity services.</summary>
    public GetBlogDetailUseCase(
        IBlogRepository blogRepository,
        ICurrentUserService currentUserService)
    {
        _blogRepository = blogRepository;
        _currentUserService = currentUserService;
    }

    /// <inheritdoc />
    public async Task<BlogDetailDto> ExecuteAsync(long blogId, CancellationToken ct)
    {
        var blog = await _blogRepository.GetPublicDetailAsync(blogId, ct)
            ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");

        var currentUserId = _currentUserService.UserId;
        var isMine = currentUserId.HasValue && blog.AuthorId == currentUserId.Value;
        var likedByMe = currentUserId.HasValue &&
            (blog.Likes.Any(like => like.UserId == currentUserId.Value) ||
             await _blogRepository.IsLikedByUserAsync(blog.Id, currentUserId.Value, ct));

        return BlogMapper.MapToDetail(blog, likedByMe, isMine);
    }
}
