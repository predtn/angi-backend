using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;

namespace ANGI.Application.UseCases.Social.DeleteBlog;

/// <summary>Implements BLOG-07 as an ownership-scoped, atomic soft deletion.</summary>
public sealed class DeleteBlogUseCase : IDeleteBlogUseCase
{
    private const string BlogNotFoundCode = "BLOG_NOT_FOUND";
    private const string BlogNotFoundMessage = "Không tìm thấy blog.";
    private const string NotOwnerCode = "NOT_OWNER";
    private const string NotOwnerMessage = "Không phải chủ của tài nguyên.";
    private const string UnauthorizedCode = "UNAUTHORIZED";
    private const string UnauthorizedMessage = "Bạn cần đăng nhập để xóa blog.";

    private readonly IBlogRepository _blogRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes identity, persistence, transaction, and clock dependencies.</summary>
    public DeleteBlogUseCase(
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

    /// <summary>Verifies ownership, records UTC deletion time, and commits the soft deletion.</summary>
    public async Task ExecuteAsync(long blogId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException(UnauthorizedCode, UnauthorizedMessage);

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

        var blog = await _blogRepository.GetTrackedByIdAsync(blogId, ct)
            ?? throw new NotFoundException(BlogNotFoundCode, BlogNotFoundMessage);

        if (blog.AuthorId != userId)
        {
            throw new ForbiddenException(NotOwnerCode, NotOwnerMessage);
        }

        blog.DeletedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
