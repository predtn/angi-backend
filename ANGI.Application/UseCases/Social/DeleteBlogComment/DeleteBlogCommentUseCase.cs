using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;

namespace ANGI.Application.UseCases.Social.DeleteBlogComment;

/// <summary>Implements BLOG-11: soft-deletes an owned blog comment and decrements comment count.</summary>
public sealed class DeleteBlogCommentUseCase : IDeleteBlogCommentUseCase
{
    private readonly IBlogCommentRepository _blogCommentRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes dependencies for BLOG-11 use case.</summary>
    public DeleteBlogCommentUseCase(
        IBlogCommentRepository blogCommentRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _blogCommentRepository = blogCommentRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(long blogId, long commentId, CancellationToken ct)
    {
        var currentUserId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xóa bình luận.");

        var comment = await _blogCommentRepository.GetByIdAndBlogIdAsync(commentId, blogId, ct)
            ?? throw new NotFoundException("COMMENT_NOT_FOUND", "Bình luận không tồn tại.");

        if (comment.UserId != currentUserId)
        {
            throw new ForbiddenException("NOT_OWNER", "Bạn không có quyền xóa bình luận này.");
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        await _blogCommentRepository.TrySoftDeleteAsync(commentId, blogId, _timeProvider.GetUtcNow().UtcDateTime, ct);
        await transaction.CommitAsync(ct);
    }
}
